# Google Drive 和 OneDrive 直连

从欢迎页的网盘菜单或主窗口的密码库菜单选择「直连网盘密码库」。在浏览器授权后选择 KDBX，KeeDesk 会下载一份加密的本地工作副本。仍需输入密码库原来的主密码，并在使用密钥文件时提供原密钥文件。下次可以从「最近打开」进入本地副本。

从主窗口选择「上传加密副本到网盘」，可将已保存的密码库上传为一个带时间戳的新 KDBX。每次上传会创建一个独立文件。直连支持不超过 64 MiB 的 KDBX 文件。

## 在多台设备同步

1. 选定一个已有的云端 KDBX 作为同步起点，或先通过「上传加密副本到网盘」创建一个。各设备使用同一网盘账号和同一 OAuth 客户端 ID，打开这份云端文件的本地副本。
2. 编辑后，在主窗口选择「直连同步网盘密码库」，再选中同一个云端起点。当前库和云端修订版必须沿用相同的主密码和密钥文件。新设备首次打开后也可执行这一步，以取得已发布的修订版。
3. KeeDesk 把可见的云端改动合并进本地库，再按需发布一份新的 KDBX 修订版。两台设备同时提交时，各自的修订版都会保留。下一次同步合并并发分支。同一条目在同一秒被不同设备修改时，另一份内容保存在条目历史中。

Google Drive 的修订版保存在该账号的 Drive 中，OneDrive 的修订版保存在应用专用文件夹。修订版沿用 KDBX 加密，并以 `KeeDesk-Sync-` 开头。原始 KDBX 仍是同步起点，其他客户端直接打开这个文件时会读到原文件内容。各设备应保留云端原文件和修订版。

## Google Drive

1. 在 Google Cloud 项目中启用 Drive API 和 Google Picker API，配置 OAuth 同意屏幕，并注册「桌面应用」类型的 OAuth 客户端。若项目处于测试模式，将使用的 Google 账号加入测试用户。 [桌面端授权](https://developers.google.com/identity/protocols/oauth2/native-app) · [配置 Google Picker](https://developers.google.com/workspace/drive/picker/guides/desktop-mobile-picker)
2. 在 KeeDesk 的网盘窗口填入客户端 ID。Google 配置若要求客户端密钥，填入该 OAuth 客户端的密钥。
3. 在系统浏览器中选择并授权 KDBX。上传的新副本位于 Google Drive 中，应用只请求 `drive.file` 权限。

## Microsoft OneDrive

1. 在 Microsoft Entra 注册原生桌面应用。选择包含个人 Microsoft 账号的受支持账户类型，在「移动和桌面应用程序」平台配置中添加重定向 URI `http://localhost`。添加 Microsoft Graph 的委托权限 `Files.ReadWrite.AppFolder`。 [注册桌面应用](https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-registration) · [OneDrive 应用文件夹](https://learn.microsoft.com/graph/onedrive-sharepoint-appfolder)
2. 在 KeeDesk 的网盘窗口填入应用程序（客户端）ID，再用个人 Microsoft 账号授权。
3. 可从 OneDrive 的 `Apps/<注册的应用名称>` 文件夹打开 KDBX。已有密码库放入该应用文件夹后即可选择。上传的副本也保存在这里。

应用可以读取进程环境变量 `KEEDESK_GOOGLE_CLIENT_ID`、`KEEDESK_GOOGLE_CLIENT_SECRET` 和 `KEEDESK_ONEDRIVE_CLIENT_ID`，以便开发或分发时预填。授权使用系统浏览器及 PKCE，访问令牌只在当前对话框生命周期内保留。
