//! Logging into the handle's event queue, so the game can show iroh's and our own diagnostics
//! in its log without us calling into it.

use std::{fmt::Write, sync::Arc};

use tracing::{
    Dispatch, Level, Subscriber,
    field::{Field, Visit},
};
use tracing_subscriber::{EnvFilter, Layer, layer::Context, prelude::*, registry::Registry};

use crate::node::{Event, Shared};

pub const DEFAULT_FILTER: &str = "warn,yarg_link=info";

/// A dispatcher for one handle's threads. Returns an error if the filter doesn't parse.
pub fn dispatch(filter: &str, shared: Arc<Shared>) -> Result<Dispatch, String> {
    let filter = EnvFilter::builder()
        .parse(filter)
        .map_err(|e| format!("bad log_filter \"{filter}\": {e}"))?;
    Ok(Dispatch::new(Registry::default().with(filter).with(QueueLayer { shared })))
}

pub fn level_code(level: &Level) -> u32 {
    match *level {
        Level::ERROR => 1,
        Level::WARN => 2,
        Level::INFO => 3,
        Level::DEBUG => 4,
        Level::TRACE => 5,
    }
}

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
        self.shared.push_log(Event::Log(level_code(meta.level()), line));
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
