//! The error type of the library.
//!
//! Each error has the C status code (`YL_ERR_*`) that the exported function returns, and a message.
//! `guard` in `lib.rs` keeps the message for `yl_last_error`.

use crate::{
    YL_ERR_BAD_CONFIG, YL_ERR_BAD_TICKET, YL_ERR_BIND, YL_ERR_BUFFER_TOO_SMALL, YL_ERR_CLOSED, YL_ERR_INTERNAL,
    YL_ERR_INVALID_ARG, YL_ERR_TOO_LARGE,
};

/// A failure, with the C status code that the exported function returns for it.
#[derive(Debug)]
pub struct YlError {
    pub code: i32,
    pub message: String,
}

impl YlError {
    fn new(code: i32, message: impl Into<String>) -> Self {
        Self { code, message: message.into() }
    }

    /// `YL_ERR_INVALID_ARG`: a null pointer, an unknown handle or channel, or a similar caller error.
    pub fn invalid_arg(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_INVALID_ARG, message)
    }

    /// `YL_ERR_BAD_CONFIG`: the configuration of `yl_create` is not correct.
    pub fn bad_config(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_BAD_CONFIG, message)
    }

    /// `YL_ERR_BIND`: the endpoint cannot open its sockets.
    pub fn bind(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_BIND, message)
    }

    /// `YL_ERR_BAD_TICKET`: the ticket of `yl_connect` is not a ticket that this library can use.
    pub fn bad_ticket(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_BAD_TICKET, message)
    }

    /// `YL_ERR_BUFFER_TOO_SMALL`: the caller's buffer is too small for the event.
    pub fn buffer_too_small(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_BUFFER_TOO_SMALL, message)
    }

    /// `YL_ERR_TOO_LARGE`: the message is larger than its channel allows.
    pub fn too_large(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_TOO_LARGE, message)
    }

    /// `YL_ERR_CLOSED`: `yl_destroy` closed this handle.
    pub fn closed(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_CLOSED, message)
    }

    /// `YL_ERR_INTERNAL`: a failure that the caller cannot fix, for example no threads for the runtime.
    pub fn internal(message: impl Into<String>) -> Self {
        Self::new(YL_ERR_INTERNAL, message)
    }
}

pub type Result<T> = std::result::Result<T, YlError>;
