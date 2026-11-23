# Changelog

## 1.3.1 - 2026-11-23

- **Welcome screen.** Aligned the actions for opening, creating, and connecting to vaults in a compact sidebar. Highlighted the selected recent vault, added tooltips for long names, and aligned the lock icon with the unlock heading.
- **Menus.** Softened the borders on context menus and flyouts, including quick settings and the vault switcher. Added shadows to nested menus.
- **Language switching.** Reduced the pause when switching between English and Chinese. macOS menu bar headings now update alongside their menu items.
- **Windows verification.** Verified screenshot protection on Windows 11 with desktop captures. Verified vault unlock with a temporary Windows account key, both alone and with a master password.

## 1.3.0 - 2026-11-23

- **Vault synchronization.** Sync two local vaults or work from a local encrypted copy of an HTTPS/WebDAV vault. KeeDesk detects conflicting writes and can merge changes or save a recovery copy.
- **Google Drive and OneDrive Personal.** Open a vault, upload an encrypted copy, and sync across devices using the same account. KeeDesk merges encrypted revisions while preserving the original cloud file. Direct connections require OAuth client configuration. Sync has been tested with simulated providers, and live-account verification is pending. [Setup and usage](https://github.com/pengpercy/KeeDesk/blob/v1.3.0/docs/cloud-drive.md)
- **Migration, printing, and attachments.** Import KeePass 1.x, Bitwarden unencrypted JSON, and 1Password 1PUX. Preview CSV imports and map fields before importing. Print vault reports, use XSLT to customize XML exports, and preview images or edit text attachments inside KeeDesk.
- **Search and audit.** Combine search terms, quoted phrases, and exclusions, or use regex and XPath. Save search profiles in the vault and check for similar passwords.
- **Auto-Type.** Customize the global shortcut, match entries across open vaults, and use window-specific sequences or selected password characters.
- **Platform fixes.** Fixed a crash when using Touch ID on the macOS welcome screen. Added an optional Windows setting to hide KeeDesk windows from supported screenshot, recording, and screen-sharing tools. The Windows desktop capture behavior is pending verification.

## 1.2.1 - 2026-11-23

- **Language switching** — Completed English translations across the workspace and dialogs. Selected drop-down labels refresh immediately when the language changes, and the macOS application menu follows the in-app language.
- **Windows title bar** — Kept the search field and toolbar buttons clickable, aligned the window controls, and restored the red close-button hover state.
- **Fluent details** — Kept text field borders consistent during the focus animation, rounded sidebar group highlights, and refined the vault switcher's icons and alignment.

## 1.2.0 - 2026-11-22

- **KeeDesk** — New name for the local KeePass-compatible password manager. Existing vaults, preferences, and recent files carry over. macOS retains its Touch ID keychain integration.
- **Window layout** — The welcome window opens at 1080 × 720, and the workspace restores its last size. An animated transition connects the two screens after unlocking. Linux follows the desktop's window button placement.
- **Fluent 2 controls** — Refined settings, menus, vault switching, and the entry detail pane. Selectors share consistent borders and shadows, text fields animate their focus indicator, and verification code progress runs smoothly.
- **Windows upgrade** — When upgrading from KeePassModern, close the old app, extract the KeeDesk ZIP, and launch `KeeDesk.exe`.

## 1.1.0 - 2026-11-22

- **Fluent 2 V3 Workspace**: Added icon-only title-bar commands with tooltips, a full-height three-pane layout on macOS, a title-bar back button in narrow desktop layouts, and a new default accent (`#508252`).
- **Welcome and Entry Workflows**: Refined the unlock screen with a narrower sidebar, and updated search suggestions, the entry editor, security audit, password generator, and settings to match the V3 design.
- **Built-in Demo Vaults**: Open sample personal and work vaults from the welcome screen to explore favorites, TOTP, and security audit findings with example data.

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
