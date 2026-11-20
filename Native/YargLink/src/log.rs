//! Logging into the handle's event queue, so the game can show iroh's and our own diagnostics
//! in its log without us calling into it.
//!
//! Each log line becomes a `YL_EVENT_LOG` event in the same queue as the other events. The game
//! reads the queue with `yl_poll`. Logging has these limits:
//!
//! - The `log_filter` of the configuration selects the lines. `EnvFilter` parses it, and `yl_create`
//!   fails with `YL_ERR_BAD_CONFIG` when it does not parse. There is no fallback to the default.
//! - The queue holds at most `MAX_QUEUED_LOGS` log lines. More lines are dropped. Thus log lines
//!   cannot fill the memory when the game does not poll. They do not count against a peer's
//!   message limit, and they cannot push out messages.
//! - `Shared::push_log` holds the queue's lock only to add one line. The lock is the same one that
//!   `yl_poll` and the peer tasks hold briefly, so log lines and other events stay in order.

use std::{fmt::Write, sync::Arc};

use tracing::{
    Dispatch, Level, Subscriber,
    field::{Field, Visit},
};
use tracing_subscriber::{EnvFilter, Layer, layer::Context, prelude::*, registry::Registry};

use crate::{
    YL_LOG_DEBUG, YL_LOG_ERROR, YL_LOG_INFO, YL_LOG_TRACE, YL_LOG_WARN,
    node::Shared,
};

pub const DEFAULT_FILTER: &str = "warn,yarg_link=info";

/// A dispatcher for one handle's threads. Returns an error if the filter doesn't parse.
pub fn dispatch(filter: &str, shared: Arc<Shared>) -> Result<Dispatch, String> {
    let filter = EnvFilter::builder()
        .parse(filter)
        .map_err(|e| format!("bad log_filter \"{filter}\": {e}"))?;
    Ok(Dispatch::new(Registry::default().with(filter).with(QueueLayer { shared })))
}

/// The `YL_LOG_*` value of a tracing level.
pub fn level_code(level: &Level) -> u32 {
    match *level {
        Level::ERROR => YL_LOG_ERROR,
        Level::WARN => YL_LOG_WARN,
        Level::INFO => YL_LOG_INFO,
        Level::DEBUG => YL_LOG_DEBUG,
        Level::TRACE => YL_LOG_TRACE,
    }
}

/// Puts each log line that passes the filter into the handle's event queue.
struct QueueLayer {
    shared: Arc<Shared>,
}

impl<S: Subscriber> Layer<S> for QueueLayer {
    fn on_event(&self, event: &tracing::Event<'_>, _ctx: Context<'_, S>) {
        let meta = event.metadata();
        let mut visitor = LineVisitor::default();
        event.record(&mut visitor);

        let mut line = format!("{}: {}", meta.target(), visitor.message);
        line.push_str(&visitor.fields);
        self.shared.push_log(level_code(meta.level()), line);
    }
}

#[derive(Default)]
struct LineVisitor {
    message: String,
    fields: String,
}

impl Visit for LineVisitor {
    fn record_str(&mut self, field: &Field, value: &str) {
        if field.name() == "message" {
            self.message.push_str(value);
        } else {
            let _ = write!(self.fields, " {}={value}", field.name());
        }
    }

    fn record_debug(&mut self, field: &Field, value: &dyn std::fmt::Debug) {
        if field.name() == "message" {
            let _ = write!(self.message, "{value:?}");
        } else {
            let _ = write!(self.fields, " {}={value:?}", field.name());
        }
    }
}
