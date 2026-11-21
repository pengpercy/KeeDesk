# Changelog

## 1.0.1 - 2026-11-22

- **Frosted Glass Context & Flyout Menus**: Refined all right-click context menus and popup flyouts to use compact `12px` typography, `6px` rounded containers, `4px` row highlights, `13px` Fluent vector icons, and native frosted-glass blur (`NSVisualEffectMaterialMenu` on macOS, DWM rounded Acrylic on Windows 11).
- **Streamlined Settings Flyout Panel**: Simplified the bottom-left quick settings flyout to focus on Appearance Mode (System / Light / Dark), UI Density (Compact / Normal / Comfortable), Check for Updates, About, and Preferences.
- **Configurable Idle Auto-Lock**: Added an explicit toggle switch in Preferences (`Security & Auto-Lock`) to enable or disable automatic vault locking after user inactivity (mouse, wheel, or keyboard), alongside the configurable timeout in minutes.

## 1.0.0 - 2026-11-22

- **Full-Featured Cross-Platform KeePass 2.x Rewrite**: Built from the ground up with Avalonia 12.1.6, .NET 10, and NativeAOT for sub-100ms cold startup and minimal memory footprint across macOS, Windows, and Linux.
- **100% KDBX 3.1 / 4.0 / 4.1 Compatibility**: Supports AES-256-CBC, ChaCha20 (RFC 7539), and Twofish-256-CBC outer ciphers; Argon2d / Argon2id and AES-KDF key derivation;ChaCha20 and Salsa20 inner stream protection; GZip compression; and Composite Master Keys (Master Password + Key File v1/v2 XML/Binary/Hex + Windows DPAPI Current User Account Key).
- **Microsoft Fluent 2 UI & Runtime Customization**: Instant runtime switching between Light / Dark / System themes, Compact / Normal / Comfortable UI densities, and Simplified Chinese (`zh-CN`) / English (`en-US`) languages without restart.
- **Native Biometrics & Platform Security**: Touch ID unlock with macOS Keychain integration, Windows DPAPI User Account Key, automatic vault locking on system sleep / screen lock / idle timeout, and concealed clipboard writes (`org.nspasteboard.ConcealedType` on macOS, `ExcludeClipboardContentFromMonitorProcessing` on Windows) with countdown auto-clear.
- **Global Auto-Type & Entry Workflows**: OS-level global Auto-Type shortcut (`Ctrl+Alt+A` / `⌃⌥A`) with wildcard window matching and sequence parser (`{USERNAME}{TAB}{PASSWORD}{ENTER}`, `{TOTP}`, `{DELAY=N}`), built-in RFC 6238 TOTP authenticator, `{REF:Field@Search:Text}` cross-entry references, automatic website favicon fetching from entry URL or email domain, multi-vault tabs, security audit center, database merge/maintenance, and CSV / XML / HTML import & export.
