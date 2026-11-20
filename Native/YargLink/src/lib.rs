//! YargLink: the peer-to-peer transport behind YARG's online play, as a C library.
//!
//! `include/yarg_link.h` is the interface. This file has the exported functions. Each one:
//!
//! 1. runs its body in `guard`, which turns errors into status codes and catches panics, so that
//!    nothing unwinds into the game;
//! 2. checks all pointer arguments first, before it does any other work;
//! 3. finds its `Node` through the handle registry (`node`), not through the pointer.
//!
//! # Handles
//!
//! A `yl_handle *` is not a memory address. It is a number that `yl_create` gives, cast to a
//! pointer type. The registry maps each number to its `Node`. Thus a wrong, old or destroyed
//! handle cannot cause a memory error: the registry does not find it, and the function returns an
//! error. The library never reads or writes memory through a handle.
//!
//! # Pointer arguments
//!
//! The other pointer arguments point to the caller's memory, and the library reads or writes
//! through them. Rust cannot check that such a pointer is valid, so the caller must make sure of
//! it. Each function's `# Safety` section gives the rules. The library checks each pointer for
//! null before it does any other work.

mod config;
mod error;
mod log;
mod node;
mod ticket;

use std::{
    cell::RefCell,
    collections::BTreeMap,
    ffi::{CStr, c_char},
    panic::{AssertUnwindSafe, catch_unwind},
    ptr, slice,
    sync::{Arc, Mutex, MutexGuard},
    thread,
    time::Duration,
};

pub use config::Config;
use error::{Result, YlError};
pub use node::{MAX_RELIABLE_FRAME, MAX_UNRELIABLE_FRAME};
use node::{Event, Node};

// The constants of `yarg_link.h`. The header has its own copy of each value, because C cannot read
// Rust. The test `header_constants_match` checks that the two copies have the same values.

pub const YL_ABI_VERSION: u32 = 4;

pub const YL_MAX_UNRELIABLE: usize = MAX_UNRELIABLE_FRAME;
pub const YL_MAX_RELIABLE: usize = MAX_RELIABLE_FRAME - 1;

pub const YL_OK: i32 = 0;
pub const YL_ERR_INVALID_ARG: i32 = -1;
pub const YL_ERR_BAD_CONFIG: i32 = -2;
pub const YL_ERR_BIND: i32 = -3;
pub const YL_ERR_BAD_TICKET: i32 = -4;
pub const YL_ERR_BUFFER_TOO_SMALL: i32 = -5;
pub const YL_ERR_TOO_LARGE: i32 = -6;
pub const YL_ERR_CLOSED: i32 = -7;
pub const YL_ERR_PANIC: i32 = -8;
pub const YL_ERR_INTERNAL: i32 = -9;

pub const YL_CHANNEL_RELIABLE: u8 = 0;
pub const YL_CHANNEL_UNRELIABLE: u8 = 1;

pub const YL_EVENT_PEER_CONNECTED: u32 = 1;
pub const YL_EVENT_PEER_DISCONNECTED: u32 = 2;
pub const YL_EVENT_MESSAGE: u32 = 3;
pub const YL_EVENT_ERROR: u32 = 4;
pub const YL_EVENT_LOG: u32 = 5;

pub const YL_LOG_ERROR: u32 = 1;
pub const YL_LOG_WARN: u32 = 2;
pub const YL_LOG_INFO: u32 = 3;
pub const YL_LOG_DEBUG: u32 = 4;
pub const YL_LOG_TRACE: u32 = 5;

pub const YL_PATH_UNKNOWN: u32 = 0;
pub const YL_PATH_DIRECT: u32 = 1;
pub const YL_PATH_RELAY: u32 = 2;

/// `yl_poll` filled the event and the buffer.
const POLL_EVENT: i32 = 1;
/// `yl_poll` found no event.
const POLL_EMPTY: i32 = 0;

/// `yl_stats.rtt_us` when QUIC has not measured the round-trip time yet.
const RTT_NOT_MEASURED: i64 = -1;

/// How long `yl_destroy` sleeps between two checks for other calls that use the node.
const DESTROY_WAIT_STEP: Duration = Duration::from_millis(1);

/// The type behind `yl_handle *`. C sees only a pointer to it, and the library never reads or
/// writes through that pointer. Refer to "Handles" at the top of this file.
pub struct YlHandle {
    /// A private field of size zero. Because of it, no code outside this module can make a
    /// `YlHandle`, and a `YlHandle` has no size. Thus only pointers to `YlHandle` exist, and C sees
    /// an opaque type (`typedef struct yl_handle yl_handle`). This is the usual Rust form of an
    /// opaque C type.
    _private: [u8; 0],
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct YlEvent {
    pub kind: u32,
    pub peer: u32,
    pub channel: u32,
    pub log_level: u32,
    pub len: u64,
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct YlStats {
    pub connected: u32,
    pub path: u32,
    pub rtt_us: i64,
    pub bytes_sent: u64,
    pub bytes_received: u64,
}

impl YlStats {
    /// The statistics of a peer that is not connected now: unknown, still connecting, or closed.
    const NOT_CONNECTED: YlStats =
        YlStats { connected: 0, path: YL_PATH_UNKNOWN, rtt_us: RTT_NOT_MEASURED, bytes_sent: 0, bytes_received: 0 };
}

/// The live handles. The key is the number that the handle pointer holds.
struct Registry {
    nodes: BTreeMap<usize, Arc<Node>>,
    /// The number for the next handle. Numbers are never used again, so an old handle never finds
    /// a new node.
    next: usize,
}

impl Registry {
    /// Add a node, and give its number. The number is new: no handle had it before.
    fn add(&mut self, node: Arc<Node>) -> usize {
        let id = self.next;
        self.next += 1;
        self.nodes.insert(id, node);
        id
    }

    /// The node with number `id`, or why there is none.
    fn get(&self, id: usize) -> Result<Arc<Node>> {
        match self.nodes.get(&id) {
            Some(node) => Ok(node.clone()),
            None if id < self.next => Err(YlError::closed("the handle is destroyed")),
            None => Err(YlError::invalid_arg("not a handle from yl_create")),
        }
    }
}

static REGISTRY: Mutex<Registry> = Mutex::new(Registry { nodes: BTreeMap::new(), next: 1 });

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

/// Lock `m`, also when it is "poisoned".
///
/// A mutex is poisoned when a thread panics while it holds the lock. After that, `Mutex::lock`
/// returns an error. Poison is not a memory-safety check: Rust keeps the data memory-safe after a
/// panic in all cases. Poison only tells the next user that the code under the lock did not finish,
/// so the data can break a rule of the program. Thus the question is whether a partial change can
/// break a rule that matters. It cannot, for these reasons:
///
/// 1. The code under each lock does not panic. The locks are `REGISTRY`, and `Shared::events`,
///    `Shared::peers` and `Peer::state` in `node`. Under them, the code only reads and changes a
///    `BTreeMap`, a `HashMap`, a `VecDeque`, an enum and counters, and copies bytes. These
///    operations panic only when memory allocation fails, and then Rust aborts the process: it does
///    not unwind, so no lock is poisoned. The counters stay far below their types' limits (the
///    queue caps them), so they do not overflow.
/// 2. If a bug adds code that panics under a lock, the worst result is small. A count can be wrong
///    by one (a queue cap is reached one message early or late), or a peer can miss its
///    `Disconnected` event. Memory stays safe, and no lock stays held: the guard is dropped when the
///    panic unwinds.
///
/// The other choice is to treat poison as fatal. Then each later call that takes the lock panics
/// too, and gives `YL_ERR_PANIC`. That includes `yl_destroy`: its `Drop` of `Node` would panic
/// before it stops the runtime, so the library's threads and sockets would stay until the game
/// exits. Thus to ignore the poison is the safer failure. `guard` still reports the first panic as
/// `YL_ERR_PANIC`, so the game knows that something went wrong.
pub(crate) fn lock_ignoring_poison<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

fn set_last_error(message: String) {
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
}

/// Run an exported function's body, and give its status code for C.
///
/// The status code is:
/// - the `Ok` value of `f`: `YL_OK`, or for `yl_poll` `POLL_EVENT` or `POLL_EMPTY`;
/// - the `YL_ERR_*` code of the error, when `f` returns an error. The error's message is then the
///   text of `yl_last_error`;
/// - `YL_ERR_PANIC`, when `f` panics. The panic does not go into the caller.
fn guard(f: impl FnOnce() -> Result<i32>) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(code)) => code,
        Ok(Err(e)) => {
            set_last_error(e.message);
            e.code
        }
        Err(panic) => {
            let message = panic
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".into());
            set_last_error(format!("panic in yarg_link: {message}"));
            YL_ERR_PANIC
        }
    }
}

/// Find the node of handle `h` in the registry. This is the check of each handle argument.
///
/// The function does not read memory at `h`. It uses only the number in `h`. Thus any value of `h`
/// is safe: null, a destroyed handle and a random value each give an error.
fn node(h: *mut YlHandle) -> Result<Arc<Node>> {
    if h.is_null() {
        return Err(YlError::invalid_arg("null handle"));
    }
    lock_ignoring_poison(&REGISTRY).get(h.addr())
}

/// Add a node to the registry, and give its handle.
fn register(node: Node) -> *mut YlHandle {
    let id = lock_ignoring_poison(&REGISTRY).add(Arc::new(node));
    // The value is a number, not an address: refer to "Handles" at the top of this file
    ptr::without_provenance_mut(id)
}

/// Remove the node of handle `h` from the registry, and give it. `None` if `h` is not a live
/// handle. After this, no call can find the node, but calls that found it before can still use it.
fn unregister(h: *mut YlHandle) -> Option<Arc<Node>> {
    lock_ignoring_poison(&REGISTRY).nodes.remove(&h.addr())
}

/// Wait until no other call uses `node`, and give the `Node`. `yl_destroy` uses this.
///
/// This function blocks the thread. Each call that found the node in the registry has a clone of
/// the `Arc` until it returns. `Arc::try_unwrap` gives the `Node` only when no other clone exists.
/// Else it gives the same `Arc` back: keep it, sleep `DESTROY_WAIT_STEP`, and try again.
///
/// The wait is short. No exported function waits for the network (refer to `Node`), so each call
/// that has a clone returns soon. The runtime's tasks have no clone (they have `Arc<Shared>`), so
/// they do not make the wait longer.
fn wait_until_unused(mut node: Arc<Node>) -> Node {
    loop {
        match Arc::try_unwrap(node) {
            Ok(node) => return node,
            Err(still_in_use) => {
                node = still_in_use;
                thread::sleep(DESTROY_WAIT_STEP);
            }
        }
    }
}

/// Give text to the caller, with the rule of "Text and data outputs" in `yarg_link.h`.
///
/// The steps are, in this order:
/// 1. Check `buf` and `len`, and set `*len` to 0. Thus `*len` is 0 after each failure: the caller
///    cannot read an old length by mistake.
/// 2. Call `text` to get the text. This is the work that can fail, for example an unknown handle.
///    On a failure, the function stops here.
/// 3. Copy the text into `buf`. If the text is longer than `cap`, copy only the first `cap` bytes.
/// 4. Set `*len` to the full length of the text, so the caller can see that the text is cut and
///    how much space it needs.
///
/// One function does all the steps, so that each exported function does them in the same order.
///
/// # Safety
/// - `buf` must be null, or valid for writes of `cap` bytes.
/// - `len` must be null, or valid for a write of one `usize`.
unsafe fn text_output(buf: *mut c_char, cap: usize, len: *mut usize, text: impl FnOnce() -> Result<String>) -> Result<i32> {
    if buf.is_null() {
        return Err(YlError::invalid_arg("null buffer"));
    }
    if len.is_null() {
        return Err(YlError::invalid_arg("null length pointer"));
    }
    // SAFETY: len is not null (checked above), and the caller makes sure that it is valid for a write.
    unsafe { *len = 0 };

    let text = text()?;

    let n = text.len().min(cap);
    // SAFETY: buf is not null (checked above), and the caller makes sure that it is valid for `cap`
    // bytes. n is at most cap, so the copy stays in the buffer. n is at most text.len(), so the copy
    // stays in text. A Rust String cannot overlap the caller's buffer.
    unsafe { ptr::copy_nonoverlapping(text.as_ptr(), buf.cast::<u8>(), n) };
    // SAFETY: len is not null (checked above), and the caller makes sure that it is valid.
    unsafe { *len = text.len() };
    Ok(YL_OK)
}

/// The C form of an event, and its payload.
fn to_c_event(event: &Event) -> (YlEvent, &[u8]) {
    match event {
        Event::Connected(peer) => (YlEvent { kind: YL_EVENT_PEER_CONNECTED, peer: *peer, ..YlEvent::default() }, &[]),
        Event::Disconnected(peer, reason) => (
            YlEvent { kind: YL_EVENT_PEER_DISCONNECTED, peer: *peer, ..YlEvent::default() },
            reason.as_bytes(),
        ),
        Event::Message(peer, channel, data) => (
            YlEvent { kind: YL_EVENT_MESSAGE, peer: *peer, channel: u32::from(*channel), ..YlEvent::default() },
            data,
        ),
        Event::Error(message) => (YlEvent { kind: YL_EVENT_ERROR, ..YlEvent::default() }, message.as_bytes()),
        Event::Log(level, line) => {
            (YlEvent { kind: YL_EVENT_LOG, log_level: *level, ..YlEvent::default() }, line.as_bytes())
        }
    }
}

/// A duration in microseconds, as `yl_stats.rtt_us`. A value too large for `i64` gives `i64::MAX`.
fn micros(duration: Duration) -> i64 {
    i64::try_from(duration.as_micros()).unwrap_or(i64::MAX)
}

#[unsafe(no_mangle)]
pub extern "C" fn yl_abi_version() -> u32 {
    YL_ABI_VERSION
}

/// # Safety
/// - `buf` must not be null. It must be valid for writes of `cap` bytes.
/// - `len` must not be null. It must be valid for a write of one `size_t`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_last_error(buf: *mut c_char, cap: usize, len: *mut usize) -> i32 {
    guard(|| {
        // SAFETY: the caller makes sure that buf and len are valid (refer to # Safety).
        unsafe { text_output(buf, cap, len, || Ok(LAST_ERROR.with(|e| e.borrow().clone()))) }
    })
}

/// # Safety
/// - `config_json` must be null, or point to text that ends with a zero byte. The text must stay
///   the same, and stay valid, until the function returns.
/// - `out` must not be null. It must be valid for a write of one pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_create(config_json: *const c_char, out: *mut *mut YlHandle) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(YlError::invalid_arg("null out pointer"));
        }
        // Set *out to NULL before the work that can fail. Thus after a failure, *out is NULL, and
        // the caller does not keep an old or random value as a handle.
        // SAFETY: out is not null (checked above), and the caller makes sure that it is valid.
        unsafe { *out = ptr::null_mut() };

        let config = if config_json.is_null() {
            Config::default()
        } else {
            // SAFETY: config_json is not null, and the caller makes sure that it points to text that
            // ends with a zero byte and does not change. CStr::from_ptr reads up to that zero byte.
            let text = unsafe { CStr::from_ptr(config_json) }
                .to_str()
                .map_err(|_| YlError::bad_config("config isn't UTF-8"))?;
            Config::from_json(text)?
        };

        let handle = register(Node::new(config)?);
        // SAFETY: out is not null (checked above), and the caller makes sure that it is valid.
        unsafe { *out = handle };
        Ok(YL_OK)
    })
}

/// Close every connection, stop all threads and remove the handle from the registry.
///
/// Any value of `h` is safe. Null, an unknown handle and a destroyed handle do nothing. The steps:
/// 1. Remove the node from the registry. After this, each new call with `h` gives `YL_ERR_CLOSED`.
/// 2. Wait until the calls on other threads that found the node before step 1 return
///    (`wait_until_unused`). This blocks, but only for a short time.
/// 3. Drop the node. `Drop` of `Node` closes the connections and stops the runtime. Thus the slow
///    close always happens on this thread, and never in another thread's call.
#[unsafe(no_mangle)]
pub extern "C" fn yl_destroy(h: *mut YlHandle) {
    guard(|| {
        let Some(node) = unregister(h) else {
            return Ok(YL_OK);
        };
        drop(wait_until_unused(node));
        Ok(YL_OK)
    });
}

/// # Safety
/// - `h` can have any value. Refer to "Handles" at the top of this file.
/// - `buf` must not be null. It must be valid for writes of `cap` bytes.
/// - `len` must not be null. It must be valid for a write of one `size_t`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_local_ticket(h: *mut YlHandle, buf: *mut c_char, cap: usize, len: *mut usize) -> i32 {
    guard(|| {
        // SAFETY: the caller makes sure that buf and len are valid (refer to # Safety).
        unsafe { text_output(buf, cap, len, || Ok(node(h)?.local_ticket())) }
    })
}

/// # Safety
/// - `h` can have any value. Refer to "Handles" at the top of this file.
/// - `ticket` must not be null. It must point to text that ends with a zero byte. The text must
///   stay the same, and stay valid, until the function returns.
/// - `peer_out` must not be null. It must be valid for a write of one `uint32_t`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_connect(h: *mut YlHandle, ticket: *const c_char, peer_out: *mut u32) -> i32 {
    guard(|| {
        if ticket.is_null() {
            return Err(YlError::invalid_arg("null ticket"));
        }
        if peer_out.is_null() {
            return Err(YlError::invalid_arg("null peer_out pointer"));
        }
        // Peer numbers start at 1, so 0 is never a peer. Thus after a failure, *peer_out is 0.
        // SAFETY: peer_out is not null (checked above), and the caller makes sure that it is valid.
        unsafe { *peer_out = 0 };
        let node = node(h)?;

        // The text comes from the player (the clipboard, a Discord invite), so ticket::parse checks
        // all of it: its length, the endpoint ID and each address.
        // SAFETY: ticket is not null, and the caller makes sure that it points to text that ends with
        // a zero byte and does not change. CStr::from_ptr reads up to that zero byte.
        let ticket = unsafe { CStr::from_ptr(ticket) }
            .to_str()
            .map_err(|_| YlError::bad_ticket("ticket isn't UTF-8"))?;
        let id = node.connect(ticket)?;
        // SAFETY: peer_out is not null (checked above), and the caller makes sure that it is valid.
        unsafe { *peer_out = id };
        Ok(YL_OK)
    })
}

/// Any value of `h` is safe. Refer to "Handles" at the top of this file.
#[unsafe(no_mangle)]
pub extern "C" fn yl_disconnect(h: *mut YlHandle, peer: u32) -> i32 {
    guard(|| {
        node(h)?.disconnect(peer);
        Ok(YL_OK)
    })
}

/// # Safety
/// - `h` can have any value. Refer to "Handles" at the top of this file.
/// - When `len` is 0, `data` can be null. When `len` is more than 0, `data` must not be null. It
///   must be valid for reads of `len` bytes, and the bytes must not change until the function
///   returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_send(h: *mut YlHandle, peer: u32, channel: u8, data: *const u8, len: usize) -> i32 {
    guard(|| {
        // A null pointer is permitted only for an empty message. This is the same rule as memcpy's.
        // C# gives a null pointer for an empty span.
        if data.is_null() && len > 0 {
            return Err(YlError::invalid_arg("null data"));
        }
        // from_raw_parts needs a length of at most isize::MAX bytes. A larger len cannot be a real
        // message, and the channel limits are much smaller.
        if len > MAX_RELIABLE_FRAME {
            return Err(YlError::too_large(format!("message of {len} bytes is too large")));
        }
        let node = node(h)?;

        let data: &[u8] = if data.is_null() {
            // An empty message. There is no memory to read, so make an empty slice (it does not
            // point to the caller's memory).
            &[]
        } else {
            // SAFETY: data is not null, and the caller makes sure that it is valid for reads of len
            // bytes, and that the bytes do not change until the function returns. len is at most
            // MAX_RELIABLE_FRAME (checked above), which is less than isize::MAX.
            unsafe { slice::from_raw_parts(data, len) }
        };
        node.send(peer, channel, data)?;
        Ok(YL_OK)
    })
}

/// # Safety
/// - `h` can have any value. Refer to "Handles" at the top of this file.
/// - `ev` must not be null. It must be valid for a write of one `yl_event`.
/// - `buf` must not be null. It must be valid for writes of `cap` bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_poll(h: *mut YlHandle, ev: *mut YlEvent, buf: *mut u8, cap: usize) -> i32 {
    guard(|| {
        if ev.is_null() {
            return Err(YlError::invalid_arg("null event pointer"));
        }
        if buf.is_null() {
            return Err(YlError::invalid_arg("null buffer"));
        }
        let node = node(h)?;

        // Hold the queue's lock until the event is copied, so that no other thread takes it first
        let mut events = node.shared().events();
        let Some(event) = events.front() else {
            // No event is waiting
            return Ok(POLL_EMPTY);
        };

        let (out, payload) = to_c_event(event);
        let out = YlEvent { len: payload.len() as u64, ..out };

        if payload.len() > cap {
            // The buffer is too small. Write *ev all the same: ev->len tells the caller how large a
            // buffer to give. Do not copy the payload, and keep the event in the queue, so that the
            // next call with a buffer of ev->len bytes gets this event.
            // SAFETY: ev is not null (checked above), and the caller makes sure that it is valid.
            unsafe { *ev = out };
            return Err(YlError::buffer_too_small(format!(
                "the event needs {} bytes, and the buffer has {cap}",
                payload.len()
            )));
        }

        // An event can have no payload (YL_EVENT_PEER_CONNECTED, or an empty message). That is not an
        // error. The copy is then 0 bytes.
        // SAFETY: buf is not null, and the caller makes sure that it is valid for cap bytes. The
        // payload is at most cap bytes (checked above). The payload is in the library's memory, so it
        // cannot overlap the caller's buffer.
        unsafe { ptr::copy_nonoverlapping(payload.as_ptr(), buf, payload.len()) };
        // SAFETY: ev is not null (checked above), and the caller makes sure that it is valid.
        unsafe { *ev = out };

        events.pop_front();
        Ok(POLL_EVENT)
    })
}

/// # Safety
/// - `h` can have any value. Refer to "Handles" at the top of this file.
/// - `buf` must not be null. It must be valid for writes of `cap` bytes.
/// - `len` must not be null. It must be valid for a write of one `size_t`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_peer_id(h: *mut YlHandle, peer: u32, buf: *mut c_char, cap: usize, len: *mut usize) -> i32 {
    guard(|| {
        let peer_id = || node(h)?.peer_id(peer).ok_or_else(|| YlError::invalid_arg("unknown or unconnected peer"));
        // SAFETY: the caller makes sure that buf and len are valid (refer to # Safety).
        unsafe { text_output(buf, cap, len, peer_id) }
    })
}

/// # Safety
/// - `h` can have any value. Refer to "Handles" at the top of this file.
/// - `out` must not be null. It must be valid for a write of one `yl_stats`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_peer_stats(h: *mut YlHandle, peer: u32, out: *mut YlStats) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(YlError::invalid_arg("null out pointer"));
        }
        let c_stats = match node(h)?.stats(peer) {
            Some(stats) => YlStats {
                connected: 1,
                path: stats.path,
                rtt_us: stats.rtt.map_or(RTT_NOT_MEASURED, micros),
                bytes_sent: stats.bytes_sent,
                bytes_received: stats.bytes_received,
            },
            None => YlStats::NOT_CONNECTED,
        };
        // SAFETY: out is not null (checked above), and the caller makes sure that it is valid.
        unsafe { *out = c_stats };
        Ok(YL_OK)
    })
}

#[cfg(test)]
mod tests;
