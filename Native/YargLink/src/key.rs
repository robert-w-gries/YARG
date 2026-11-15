//! The endpoint's secret key, kept in a file so the endpoint ID (and so the invite code) stays the
//! same between runs.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Write},
    path::{Path, PathBuf},
};

use iroh::SecretKey;

/// A key loaded from disk, plus the lock that keeps other processes from using it at the same time
/// (two endpoints with one ID would fight over it).
pub struct StoredKey {
    pub key: SecretKey,
    _lock: File,
}

/// Load the key at `path`, creating it if it doesn't exist. Returns `Ok(None)` when another process
/// holds it (for example, a second copy of the game on the same machine).
pub fn load_or_create(path: &Path) -> io::Result<Option<StoredKey>> {
    if let Some(dir) = path.parent()
        && !dir.as_os_str().is_empty()
    {
        fs::create_dir_all(dir)?;
    }

    let lock = OpenOptions::new().create(true).truncate(false).write(true).open(lock_path(path))?;
    match lock.try_lock() {
        Ok(()) => {}
        Err(fs::TryLockError::WouldBlock) => return Ok(None),
        Err(fs::TryLockError::Error(e)) => return Err(e),
    }

    let key = match fs::read_to_string(path) {
        Ok(text) => decode(text.trim()).ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidData, format!("{} is not a valid key file", path.display()))
        })?,
        Err(e) if e.kind() == io::ErrorKind::NotFound => {
            let key = SecretKey::generate();
            write_private(path, &encode(&key))?;
            key
        }
        Err(e) => return Err(e),
    };

    Ok(Some(StoredKey { key, _lock: lock }))
}

fn lock_path(path: &Path) -> PathBuf {
    let mut name = path.file_name().map(|n| n.to_os_string()).unwrap_or_default();
    name.push(".lock");
    path.with_file_name(name)
}

fn encode(key: &SecretKey) -> String {
    key.to_bytes().iter().map(|b| format!("{b:02x}")).collect()
}

fn decode(text: &str) -> Option<SecretKey> {
    if text.len() != 64 || !text.is_ascii() {
        return None;
    }
    let mut bytes = [0u8; 32];
    for (i, byte) in bytes.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&text[i * 2..i * 2 + 2], 16).ok()?;
    }
    Some(SecretKey::from_bytes(&bytes))
}

/// Write a new file only the current user can read, via a temporary file so a crash can't leave
/// a half-written key behind.
fn write_private(path: &Path, contents: &str) -> io::Result<()> {
    let tmp = path.with_extension("tmp");
    let mut options = OpenOptions::new();
    options.write(true).create(true).truncate(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(&tmp)?;
    file.write_all(contents.as_bytes())?;
    file.sync_all()?;
    drop(file);
    fs::rename(&tmp, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("yarg-link-test-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn key_is_stable_and_exclusive() {
        let dir = temp_dir("key");
        let path = dir.join("nested").join("secret.key");

        let first = load_or_create(&path).unwrap().expect("not locked");
        assert!(load_or_create(&path).unwrap().is_none(), "second load while locked");

        let id = first.key.public();
        drop(first);
        let again = load_or_create(&path).unwrap().expect("unlocked after drop");
        assert_eq!(again.key.public(), id);

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn rejects_corrupt_key() {
        let dir = temp_dir("corrupt");
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("secret.key");
        fs::write(&path, "nope").unwrap();
        assert!(load_or_create(&path).is_err());
        let _ = fs::remove_dir_all(&dir);
    }
}
