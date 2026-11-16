//! YargLink: the peer-to-peer transport behind YARG's online play, as a C library.
//!
//! See `include/yarg_link.h` for the interface. Every exported function catches panics and turns
//! them into `YL_ERR_PANIC`, so nothing ever unwinds into the game.

mod log;
mod node;
mod ticket;

use std::{
    cell::RefCell,
    ffi::{CStr, c_char},
    panic::{AssertUnwindSafe, catch_unwind},
    ptr, slice,
};

pub use node::{Config, MAX_FRAME, MAX_UNRELIABLE};
use node::{Error, Event, Node};

pub const YL_ABI_VERSION: u32 = 3;

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

pub const YL_EVENT_PEER_CONNECTED: u32 = 1;
pub const YL_EVENT_PEER_DISCONNECTED: u32 = 2;
pub const YL_EVENT_MESSAGE: u32 = 3;
pub const YL_EVENT_ERROR: u32 = 4;
pub const YL_EVENT_LOG: u32 = 5;

/// Opaque to C.
pub struct YlHandle {
    node: Node,
}

#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct YlEvent {
    pub kind: u32,
    pub peer: u32,
    pub channel: u32,
    pub level: u32,
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

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_last_error(message: String) {
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
}

/// Run an exported function's body: errors become status codes plus `yl_last_error`, panics become
/// `YL_ERR_PANIC`.
fn guard(f: impl FnOnce() -> Result<i32, Error>) -> i32 {
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

fn invalid(message: &str) -> Error {
    Error::new(YL_ERR_INVALID_ARG, message)
}

/// # Safety
/// `h` must be null or a live handle from `yl_create`.
unsafe fn handle<'a>(h: *mut YlHandle) -> Result<&'a Node, Error> {
    unsafe { h.as_ref() }.map(|h| &h.node).ok_or_else(|| invalid("null handle"))
}

/// Copy `text` into a caller's buffer, reporting the full length.
///
/// # Safety
/// `buf` must be valid for `cap` bytes (or null with `cap` 0), `len` null or valid.
unsafe fn write_text(text: &str, buf: *mut c_char, cap: usize, len: *mut usize) -> Result<i32, Error> {
    if !len.is_null() {
        unsafe { *len = text.len() };
    }
    if buf.is_null() && cap > 0 {
        return Err(invalid("null buffer"));
    }
    let n = text.len().min(cap);
    if n > 0 {
        unsafe { ptr::copy_nonoverlapping(text.as_ptr(), buf.cast::<u8>(), n) };
    }
    Ok(YL_OK)
}

#[unsafe(no_mangle)]
pub extern "C" fn yl_abi_version() -> u32 {
    YL_ABI_VERSION
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_last_error(buf: *mut c_char, cap: usize, len: *mut usize) -> i32 {
    guard(|| {
        let text = LAST_ERROR.with(|e| e.borrow().clone());
        unsafe { write_text(&text, buf, cap, len) }
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_create(config_json: *const c_char, out: *mut *mut YlHandle) -> i32 {
    guard(|| {
        if out.is_null() {
            return Err(invalid("null out pointer"));
        }
        unsafe { *out = ptr::null_mut() };

        let config: Config = if config_json.is_null() {
            Config::default()
        } else {
            let text = unsafe { CStr::from_ptr(config_json) }
                .to_str()
                .map_err(|_| Error::new(YL_ERR_BAD_CONFIG, "config isn't UTF-8"))?;
            if text.trim().is_empty() {
                Config::default()
            } else {
                serde_json::from_str(text).map_err(|e| Error::new(YL_ERR_BAD_CONFIG, format!("bad config: {e}")))?
            }
        };

        let node = Node::new(config)?;
        unsafe { *out = Box::into_raw(Box::new(YlHandle { node })) };
        Ok(YL_OK)
    })
}

/// # Safety
/// `h` must be null or a live handle from `yl_create`; it is invalid afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_destroy(h: *mut YlHandle) {
    guard(|| {
        if !h.is_null() {
            drop(unsafe { Box::from_raw(h) });
        }
        Ok(YL_OK)
    });
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_local_ticket(h: *mut YlHandle, buf: *mut c_char, cap: usize, len: *mut usize) -> i32 {
    guard(|| {
        let node = unsafe { handle(h) }?;
        unsafe { write_text(&node.local_ticket(), buf, cap, len) }
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_connect(h: *mut YlHandle, ticket: *const c_char, peer_out: *mut u32) -> i32 {
    guard(|| {
        let node = unsafe { handle(h) }?;
        if ticket.is_null() || peer_out.is_null() {
            return Err(invalid("null argument"));
        }
        let ticket = unsafe { CStr::from_ptr(ticket) }
            .to_str()
            .map_err(|_| Error::new(YL_ERR_BAD_TICKET, "ticket isn't UTF-8"))?;
        let id = node.connect(ticket)?;
        unsafe { *peer_out = id };
        Ok(YL_OK)
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_disconnect(h: *mut YlHandle, peer: u32) -> i32 {
    guard(|| {
        unsafe { handle(h) }?.disconnect(peer);
        Ok(YL_OK)
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_send(h: *mut YlHandle, peer: u32, channel: u8, data: *const u8, len: usize) -> i32 {
    guard(|| {
        let node = unsafe { handle(h) }?;
        let data = if len == 0 {
            &[][..]
        } else if data.is_null() {
            return Err(invalid("null data"));
        } else {
            unsafe { slice::from_raw_parts(data, len) }
        };
        node.send(peer, channel, data)?;
        Ok(YL_OK)
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_poll(h: *mut YlHandle, ev: *mut YlEvent, buf: *mut u8, cap: usize) -> i32 {
    guard(|| {
        let node = unsafe { handle(h) }?;
        if ev.is_null() || (buf.is_null() && cap > 0) {
            return Err(invalid("null argument"));
        }

        Ok(node.shared().with_front(|front| {
            let Some(event) = front else { return (0, false) };

            let mut out = YlEvent::default();
            let payload: &[u8] = match event {
                Event::Connected(peer) => {
                    out.kind = YL_EVENT_PEER_CONNECTED;
                    out.peer = *peer;
                    &[]
                }
                Event::Disconnected(peer, reason) => {
                    out.kind = YL_EVENT_PEER_DISCONNECTED;
                    out.peer = *peer;
                    reason.as_bytes()
                }
                Event::Message(peer, channel, data) => {
                    out.kind = YL_EVENT_MESSAGE;
                    out.peer = *peer;
                    out.channel = *channel as u32;
                    data
                }
                Event::Error(message) => {
                    out.kind = YL_EVENT_ERROR;
                    message.as_bytes()
                }
                Event::Log(level, line) => {
                    out.kind = YL_EVENT_LOG;
                    out.level = *level;
                    line.as_bytes()
                }
            };
            out.len = payload.len() as u64;
            unsafe { *ev = out };

            if payload.len() > cap {
                return (YL_ERR_BUFFER_TOO_SMALL, false);
            }
            if !payload.is_empty() {
                unsafe { ptr::copy_nonoverlapping(payload.as_ptr(), buf, payload.len()) };
            }
            (1, true)
        }))
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_peer_id(h: *mut YlHandle, peer: u32, buf: *mut c_char, cap: usize, len: *mut usize) -> i32 {
    guard(|| {
        let node = unsafe { handle(h) }?;
        match node.peer_id(peer) {
            Some(id) => unsafe { write_text(&id, buf, cap, len) },
            None => {
                if !len.is_null() {
                    unsafe { *len = 0 };
                }
                Err(invalid("unknown or unconnected peer"))
            }
        }
    })
}

/// # Safety
/// See `yarg_link.h`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn yl_peer_stats(h: *mut YlHandle, peer: u32, out: *mut YlStats) -> i32 {
    guard(|| {
        let node = unsafe { handle(h) }?;
        if out.is_null() {
            return Err(invalid("null out pointer"));
        }
        let stats = node.stats(peer);
        unsafe {
            *out = YlStats {
                connected: stats.connected as u32,
                path: stats.path,
                rtt_us: stats.rtt.map(|d| d.as_micros().min(i64::MAX as u128) as i64).unwrap_or(-1),
                bytes_sent: stats.bytes_sent,
                bytes_received: stats.bytes_received,
            }
        };
        Ok(YL_OK)
    })
}

#[cfg(test)]
mod tests;
