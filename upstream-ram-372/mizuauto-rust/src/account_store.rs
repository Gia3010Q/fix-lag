use argon2::{Algorithm, Argon2, Params, Version};
use serde_json::{Map, Value};
use sha2::{Digest, Sha512};
use std::{
    fs, io,
    path::{Path, PathBuf},
};
use xsalsa20poly1305::{
    Key, Nonce, Tag, XSalsa20Poly1305,
    aead::{AeadInPlace, KeyInit},
};
use zeroize::Zeroize;

// Constants are copied from the RAM 3.7.2 source. Account data itself is never logged.
const HEADER: &[u8] = b"Roblox Account Manager created by ic3w0lf22 @ github.com .......";
const ENTROPY: &[u8] = b"ROBLOX ACCOUNT MANAGER | :) | BROUGHT TO YOU BY ic3w0lf";

#[derive(Clone)]
pub struct AccountRecord {
    pub fields: Map<String, Value>,
}
impl AccountRecord {
    pub fn text(&self, name: &str) -> String {
        self.fields
            .get(name)
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_owned()
    }
    pub fn username(&self) -> String {
        self.text("Username")
    }
    pub fn alias(&self) -> String {
        self.text("Alias")
    }
    pub fn description(&self) -> String {
        self.text("Description")
    }
    pub fn cookie(&self) -> String {
        self.text("SecurityToken")
    }
    pub fn user_id(&self) -> u64 {
        self.fields
            .get("UserID")
            .and_then(Value::as_u64)
            .unwrap_or(0)
    }
    pub fn set_text(&mut self, name: &str, text: impl Into<String>) {
        self.fields
            .insert(name.to_owned(), Value::String(text.into()));
    }
    pub fn custom(&self, name: &str) -> String {
        self.fields
            .get("Fields")
            .and_then(Value::as_object)
            .and_then(|m| m.get(name))
            .and_then(Value::as_str)
            .unwrap_or("")
            .to_owned()
    }
    pub fn set_custom(&mut self, name: &str, text: impl Into<String>) {
        let entry = self
            .fields
            .entry("Fields".to_owned())
            .or_insert_with(|| Value::Object(Map::new()));
        if !entry.is_object() {
            *entry = Value::Object(Map::new());
        }
        if let Some(fields) = entry.as_object_mut() {
            fields.insert(name.to_owned(), Value::String(text.into()));
        }
    }
    pub fn remove_custom(&mut self, name: &str) {
        if let Some(fields) = self.fields.get_mut("Fields").and_then(Value::as_object_mut) {
            fields.remove(name);
        }
    }
    pub fn from_login(username: &str, user_id: u64, cookie: &str) -> Self {
        let mut fields = Map::new();
        fields.insert("Valid".into(), Value::Bool(true));
        fields.insert("Username".into(), Value::String(username.into()));
        fields.insert("UserID".into(), Value::Number(user_id.into()));
        fields.insert("SecurityToken".into(), Value::String(cookie.into()));
        fields.insert("Group".into(), Value::String("Default".into()));
        fields.insert("Alias".into(), Value::String(String::new()));
        fields.insert("Description".into(), Value::String(String::new()));
        fields.insert("Password".into(), Value::String(String::new()));
        fields.insert("Fields".into(), Value::Object(Map::new()));
        Self { fields }
    }
}

pub enum StorageMode {
    MachineDpapi,
    Password([u8; 64]),
    Plain,
}
impl Drop for StorageMode {
    fn drop(&mut self) {
        if let StorageMode::Password(hash) = self {
            hash.zeroize();
        }
    }
}

pub enum LoadResult {
    Ready(AccountStore),
    PasswordRequired,
}

pub struct AccountStore {
    pub path: PathBuf,
    pub records: Vec<AccountRecord>,
    mode: StorageMode,
}

impl AccountStore {
    pub fn load(path: &Path, password: Option<&str>) -> io::Result<LoadResult> {
        let bytes = match fs::read(path) {
            Ok(b) => b,
            Err(err) if err.kind() == io::ErrorKind::NotFound => Vec::new(),
            Err(err) => return Err(err),
        };
        let (mut plain, mode) = if bytes.is_empty() {
            let no_encryption = path.parent().is_some_and(|dir| {
                dir.join("NoEncryption.IUnderstandTheRisks.iautamor")
                    .exists()
            });
            (
                b"[]".to_vec(),
                if no_encryption {
                    StorageMode::Plain
                } else {
                    StorageMode::MachineDpapi
                },
            )
        } else if bytes.starts_with(HEADER) {
            let Some(password) = password else {
                return Ok(LoadResult::PasswordRequired);
            };
            if password.chars().count() < 4 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Password must be at least four characters",
                ));
            }
            let mut hashed = [0_u8; 64];
            hashed.copy_from_slice(&Sha512::digest(password.as_bytes()));
            let decoded = decrypt_password(&bytes, &hashed)?;
            (decoded, StorageMode::Password(hashed))
        } else if let Ok(decoded) = crate::platform::dpapi_unprotect(&bytes, ENTROPY) {
            (decoded, StorageMode::MachineDpapi)
        } else if serde_json::from_slice::<Value>(&bytes).is_ok() {
            (bytes, StorageMode::Plain)
        } else {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "AccountData.json could not be decoded; original file was not changed",
            ));
        };
        let decoded: Result<Vec<Map<String, Value>>, _> = serde_json::from_slice(&plain);
        plain.zeroize();
        let rows = decoded
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "Account JSON is invalid"))?;
        Ok(LoadResult::Ready(Self {
            path: path.to_path_buf(),
            records: rows
                .into_iter()
                .map(|fields| AccountRecord { fields })
                .collect(),
            mode,
        }))
    }

    pub fn save(&self) -> io::Result<()> {
        let rows: Vec<_> = self.records.iter().map(|r| &r.fields).collect();
        let mut plain = serde_json::to_vec(&rows).map_err(io::Error::other)?;
        let mut output = match &self.mode {
            StorageMode::MachineDpapi => crate::platform::dpapi_protect(&plain, ENTROPY)?,
            StorageMode::Password(hash) => encrypt_password(&plain, hash)?,
            StorageMode::Plain => std::mem::take(&mut plain),
        };
        plain.zeroize();
        if self.path.exists() {
            let backup = self.path.with_extension("json.backup");
            fs::copy(&self.path, backup)?;
        }
        let result = crate::platform::atomic_write(&self.path, &output);
        output.zeroize();
        result
    }

    pub fn add(&mut self, record: AccountRecord) {
        if let Some(existing) = self
            .records
            .iter_mut()
            .find(|r| r.user_id() == record.user_id())
        {
            existing.set_text("SecurityToken", record.cookie());
            existing.set_text("Username", record.username());
            existing.fields.insert("Valid".into(), Value::Bool(true));
        } else {
            self.records.push(record);
        }
    }

    pub fn remove_indices(&mut self, indices: &[usize]) {
        let chosen: std::collections::HashSet<usize> = indices.iter().copied().collect();
        self.records = self
            .records
            .drain(..)
            .enumerate()
            .filter_map(|(i, r)| (!chosen.contains(&i)).then_some(r))
            .collect();
    }
}

fn derive_key(hash: &[u8; 64], salt: &[u8]) -> io::Result<[u8; 32]> {
    let params = Params::new(131_072, 6, 1, Some(32)).map_err(io::Error::other)?;
    let argon = Argon2::new(Algorithm::Argon2i, Version::V0x13, params);
    let mut key = [0_u8; 32];
    argon
        .hash_password_into(hash, salt, &mut key)
        .map_err(io::Error::other)?;
    Ok(key)
}

fn decrypt_password(contents: &[u8], hash: &[u8; 64]) -> io::Result<Vec<u8>> {
    let start = HEADER.len();
    if contents.len() < start + 16 + 24 + 16 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Password-locked account file is too short",
        ));
    }
    let salt = &contents[start..start + 16];
    let nonce = &contents[start + 16..start + 40];
    let encrypted = &contents[start + 40..];
    let mut key = derive_key(hash, salt)?;
    let cipher = XSalsa20Poly1305::new(Key::from_slice(&key));
    let mut data = encrypted[16..].to_vec();
    let result = cipher.decrypt_in_place_detached(
        Nonce::from_slice(nonce),
        b"",
        &mut data,
        Tag::from_slice(&encrypted[..16]),
    );
    key.zeroize();
    result.map_err(|_| {
        io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Incorrect password or account file integrity check failed",
        )
    })?;
    Ok(data)
}

fn encrypt_password(plain: &[u8], hash: &[u8; 64]) -> io::Result<Vec<u8>> {
    let salt: [u8; 16] = rand::random();
    let nonce: [u8; 24] = rand::random();
    let mut key = derive_key(hash, &salt)?;
    let cipher = XSalsa20Poly1305::new(Key::from_slice(&key));
    let mut data = plain.to_vec();
    let tag = cipher
        .encrypt_in_place_detached(Nonce::from_slice(&nonce), b"", &mut data)
        .map_err(|_| io::Error::other("Account encryption failed"))?;
    key.zeroize();
    let mut result =
        Vec::with_capacity(HEADER.len() + salt.len() + nonce.len() + tag.len() + data.len());
    result.extend_from_slice(HEADER);
    result.extend_from_slice(&salt);
    result.extend_from_slice(&nonce);
    result.extend_from_slice(&tag);
    result.extend_from_slice(&data);
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn password_format_round_trip() {
        let hash = Sha512::digest(b"safe synthetic password");
        let mut key = [0_u8; 64];
        key.copy_from_slice(&hash);
        let data = br#"[{"Username":"fixture"}]"#;
        let encrypted = encrypt_password(data, &key).expect("encrypt");
        assert_eq!(decrypt_password(&encrypted, &key).expect("decrypt"), data);
        assert!(decrypt_password(&encrypted, &[0; 64]).is_err());
    }
}
