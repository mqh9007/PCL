# Plain Craft Launcher

[![Stars](https://img.shields.io/github/stars/Meloong-Git/PCL?style=flat&logo=data:image/svg%2bxml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZlcnNpb249IjEiIHdpZHRoPSIxNiIgaGVpZ2h0PSIxNiI+PHBhdGggZD0iTTggLjI1YS43NS43NSAwIDAgMSAuNjczLjQxOGwxLjg4MiAzLjgxNSA0LjIxLjYxMmEuNzUuNzUgMCAwIDEgLjQxNiAxLjI3OWwtMy4wNDYgMi45Ny43MTkgNC4xOTJhLjc1MS43NTEgMCAwIDEtMS4wODguNzkxTDggMTIuMzQ3bC0zLjc2NiAxLjk4YS43NS43NSAwIDAgMS0xLjA4OC0uNzlsLjcyLTQuMTk0TC44MTggNi4zNzRhLjc1Ljc1IDAgMCAxIC40MTYtMS4yOGw0LjIxLS42MTFMNy4zMjcuNjY4QS43NS43NSAwIDAgMSA4IC4yNVoiIGZpbGw9IiNlYWM1NGYiLz48L3N2Zz4=&logoSize=auto&label=Stars&labelColor=444444&color=eac54f)](https://github.com/Meloong-Git/PCL/)
[![Issues](https://img.shields.io/github/issues/Meloong-Git/PCL?style=flat&label=Issues&labelColor=444444&color=1F883D)](https://github.com/Meloong-Git/PCL/issues)
[![哔哩哔哩](https://img.shields.io/badge/动态-BiliBili-00A4DB?style=flat&labelColor=444444&logoSize=auto)](https://space.bilibili.com/11343203/dynamic)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946ce6?style=flat&labelColor=444444&logoSize=auto)](https://meloong.com/afd/a/LTCat)

包含 PCL 的大部分源代码，包括 UI 库、动画模块、下载模块、Minecraft 启动模块等！<br/>
代码并不是实时更新的，它会在每次 PCL 发布更新时同步一次。<br/>
非常感谢社区的大力支持！

<br/>

### 相关资源
- [PCL 下载](https://meloong.com/pcl)：下载可免费使用的正式版 PCL！
- [PCL 功能投票](https://github.com/Meloong-Git/PCL/discussions/2)：来参加投票吧，开发者会优先处理票数较高的项目！
- [帮助文档库](https://github.com/LTCatt/PCL2Help)：PCL 内置帮助文档的存储库

### 多人游戏服务器预设
- 在「设置 → 启动 → 多人游戏」中开启「自动添加多人游戏服务器」，并填写 `title1:serveri_ip1,title2:serveri_ip2,title3:serveri_ip3`，留空则不添加。
- 名称和地址使用第一个英文冒号分隔，多个条目使用英文逗号分隔；支持域名、IPv4、`[IPv6]` 和可选端口，例如 `生存服:play.example.com,创造服:127.0.0.1:25566`。
- 启动前写入当前版本实际游戏目录的 `servers.dat`，遵循版本隔离设置；保留原有条目、顺序、图标及其他字段，按地址去重（忽略大小写和默认端口 `25565`），不会自动连接服务器。
- 修改已有列表前自动保留备份 `servers.dat.pcl.bak`；列表损坏或不可写时提示错误并继续启动，不覆盖原文件。关闭功能或移除配置不会删除游戏中已添加的服务器。
- 在 GitHub 仓库的 **Settings → Secrets and variables → Actions → Variables** 添加仓库变量 `PCL_SERVER_LIST`，值为上述单行列表；Build 工作流将其编译为默认配置。未设置变量时默认列表为空，用户可在界面修改、清空或关闭，重置启动设置会恢复构建预设。该变量是公开配置，不应包含密码或令牌。

### 百宝箱
- 「更多 → 百宝箱」采用原版的三卡片布局：顶部为今日人品、内存优化、清理游戏垃圾和千万别点；中间是可反复点击的回声洞；底部是下载自定义文件表单。
- 下载表单支持填写地址、选择/记忆保存文件夹、自动提取文件名和打开文件夹；填写有效内容后才启用「开始下载」，手动修改的文件名不会被自动覆盖。
- 自定义下载使用启动器的下载管理，支持进度查看、取消和失败提示；自定义主页的「下载文件」「内存优化」「清理垃圾」「今日人品」事件也可使用。
- 内存优化只回收当前用户有权限访问的进程工作集，不会关闭程序，不要求管理员权限；启动前自动优化和 `--memory` 命令行入口共用此实现。
- 垃圾清理需要确认，只处理缓存目录 `Download` 中超过一天的 PCL 临时文件和 `TaskTemp` 中超过一天的任务缓存。运行中的启动/下载任务、链接目录、只读文件会被跳过，游戏和启动器设置不会被清理。
- 可在启动器旁的 `PCL\hints.txt`、`PCL\caves.txt` 中自定义随机提示和趣闻（UTF-8，每行一条，空行忽略）；文件不存在、为空或不可读时使用内置内容。自定义主页的 `{hint}`、`{cave}` 及启动时的「你知道吗」提示同步生效。

### 启动器更新

- 发布源为 `mqh9007/PCL` 的公开 GitHub Releases。由发布标签构建的正式版会接入更新源。
- 首次发布前，用 PowerShell 7 生成一次 RSA 签名密钥。以下命令将公钥写入仓库变量 `PCL_UPDATE_PUBLIC_KEY`，将私钥写入仓库 Secret `PCL_UPDATE_SIGNING_KEY`。后续发布继续使用这对密钥，并妥善保存私钥备份。

  ```powershell
  $rsa = [Security.Cryptography.RSA]::Create(3072)
  try {
      $public = $rsa.ExportParameters($false)
      $publicKey = [Convert]::ToBase64String($public.Modulus) + '.' + [Convert]::ToBase64String($public.Exponent)
      $privateKey = [Convert]::ToBase64String($rsa.ExportPkcs8PrivateKey())
      $publicKey | gh variable set PCL_UPDATE_PUBLIC_KEY --repo mqh9007/PCL
      if ($LASTEXITCODE -ne 0) { throw '写入更新公钥失败' }
      $privateKey | gh secret set PCL_UPDATE_SIGNING_KEY --repo mqh9007/PCL
      if ($LASTEXITCODE -ne 0) { throw '写入更新私钥失败' }
  } finally {
      $privateKey = $null
      $rsa.Dispose()
  }
  ```

- 将发布提交打上递增标签，如 `pcl-v1`、`pcl-v2`，并推送标签。Build 工作流会发布 `PCL.exe` 与签名清单 `update.json`。需要将本次更新标记为重大更新时，发布前把仓库变量 `PCL_UPDATE_CRITICAL` 设为 `true`，发布后清空。
- 首次手动下载并运行 Release 中的 `PCL.exe`。后续版本可从「设置 → 启动器」或「更多 → 关于」检查。启动器在退出时安装已下载的更新，并在原程序旁保留 `.pcl-update.bak` 备份。
