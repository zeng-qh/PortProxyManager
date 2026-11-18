# PortProxy Manager

通过网页管理本机 `netsh interface portproxy` 端口转发规则的 Windows 小工具。保存即立即生效，规则丢失自动补回，支持 IPv4/IPv6 四种转发模式，可注册为 Windows 服务实现开机自启，并支持局域网内其他设备远程访问。

> 典型场景：本机有多张网卡 / 多个网段（如公司内网 + 热点网段），需要把某个监听端口的流量转发到另一台内网机器（例如 `192.168.24.37:66 → 192.168.137.167:22`）。

## 功能特性

| 功能 | 说明 |
|---|---|
| 网页增删改规则 | 保存后**立即执行 netsh 写入系统**，成功/失败直接回显 |
| 转发模式自动推导 | 由两端地址族自动确定 `v4tov4 / v6tov4 / v4tov6 / v6tov6`，无需手选 |
| 自动巡检恢复 | 后台按间隔（默认 30s，可调）比对「期望规则 vs 系统实际」，网络波动导致规则丢失/漂移时自动补回，可选清理未登记规则 |
| 开机自启 | 一键注册为 **Windows 服务**（LocalSystem，无需登录、无 UAC 弹窗，崩溃自动拉起），安装后开机自动运行并恢复规则 |
| 远程访问 | Web 监听可切到 `0.0.0.0` / `::`，局域网内其他设备浏览器直接访问；自动放通端口防火墙 |
| 访问令牌鉴权 | 启用后所有 API 需携带令牌，其他设备打开页面需输入令牌登录 |
| 导入 / 导出 | 规则 JSON 一键导出为文件、批量导入（合并 / 替换，逐条校验回报） |
| 监听地址带出 | 自动列出本机网卡 IPv4 / IPv6 全局地址（虚拟网卡排后），一键 `0.0.0.0` / `::` |
| 深色主题 | 跟随系统 + 手动切换，偏好记忆 |
| 实时日志 | 页面内滚动日志 + 按天落盘 `logs/portproxy-yyyyMMdd.log` |

## 快速开始

### 下载预编译版本

推送到 `main` 会触发 GitHub Actions 自动构建三种包（仓库 **Actions → 最新 run → Artifacts** 下载）：

| 包 | 说明 |
|---|---|
| `PortProxyManager-win-x86.zip` | **32 位自包含**，免装任何环境 |
| `PortProxyManager-win-x64.zip` | **64 位自包含**，免装任何环境 |
| `PortProxyManager-framework-dependent.zip` | 依赖 .NET 10 Runtime，体积小 |

三种包都带 `start-admin.bat` 启动器，解压后双击即可（以管理员运行）。推送 `v*` 标签（如 `git tag v1.0.0 && git push --tags`）会自动创建 GitHub Release 并附上三个包。

### 运行（本地构建）

1. 双击 `publish/start-admin.bat`（以管理员拉起，netsh 写入需要提权）
2. 浏览器打开 <http://127.0.0.1:5577>
3. 点「开机自启（Windows 服务）→ 安装服务」，之后无需再手动运行

### 从源码构建

```bash
dotnet build -c Release
dotnet publish -c Release -o publish
# 运行（需管理员）
publish/PortProxyManager.exe --urls http://127.0.0.1:5577
```

环境要求：Windows 10+、.NET 10 SDK（运行仅需 .NET 10 Runtime）。

## 使用说明

### 规则管理

- **监听地址**：填本机网卡 IP（页面自动带出可下拉选），`0.0.0.0` = 所有 IPv4 网卡，`::` = 所有 IPv6
- **目标地址**：要转发到的机器 IP（支持 IPv6）
- 保存即生效；也可先「禁用」再手动「启用」
- 「全部应用」把所有已登记规则重新对齐到系统；「清空系统全部规则」会 reset 后立即按登记规则重建

### 巡检设置

- **自动巡检恢复**：开启后按间隔比对，缺失自动 `delete + add` 补回，漂移（目标变了）自动纠正
- **删除未登记规则**：把系统里存在但本工具没登记的规则一并清掉（谨慎开启，避免误删手动加的规则）

### 开机自启（Windows 服务）

- 安装 / 卸载需要管理员权限；服务以 `LocalSystem` 运行，开机自动拉起
- 服务进程崩溃 1 分钟后自动重启（`sc failure` 配置）
- 页面会检测「服务指向路径与当前程序是否一致」，不一致会提示重装
- 改了 Web 监听地址/端口后，点「重启生效」：服务模式自动 `sc stop/start`，控制台模式自动拉起新实例

### 远程访问与鉴权

1. 「巡检与访问设置」里把 Web 监听地址改为 `0.0.0.0`（或 `::`），保存
2. 启用「访问令牌」——首次自动生成令牌（可自行修改），把令牌告诉需要访问的设备
3. 其他设备浏览器打开 `http://本机IP:端口`，输入令牌即可进入管理页
4. 监听为非环回地址时会自动添加入站防火墙规则（`PortProxyManager_Web_<端口>`）

### 导入 / 导出

- **导出**：下载 `portproxy-rules-<时间>.json`
- **导入**：选择 JSON 文件 + 模式
  - `合并`：保留现有规则，相同监听键以导入为准
  - `替换`：清空现有规则，只保留导入的（会先确认）
  - 逐条校验：地址 / 端口不合法会被拒并回报原因，合法的立即应用

## API 一览

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/state` | 全量状态（设置 / 规则 / 系统实际 / 网卡 IP / 日志） |
| POST | `/api/rules` | 新增规则并立即生效 |
| PUT | `/api/rules/{id}` | 修改规则 |
| DELETE | `/api/rules/{id}` | 删除规则 |
| POST | `/api/rules/{id}/toggle` | 启用 / 禁用 |
| POST | `/api/rules/import` | 批量导入 `{replace: bool, rules: [...]}` |
| GET | `/api/rules/export` | 导出 JSON 附件 |
| POST | `/api/apply` | 全量应用 |
| POST | `/api/reset` | 清空系统规则并重建 |
| POST | `/api/settings` | 保存设置 |
| GET | `/api/service` | Windows 服务状态 |
| POST | `/api/service/install` `/uninstall` `/start` `/stop` | 服务操作 |
| POST | `/api/app/restart` | 重启程序 / 服务（监听配置变更后生效） |
| GET | `/api/auth?token=` | 令牌校验（登录用） |
| GET | `/health` | 健康检查 |

启用鉴权后，除 `/api/auth` 外的所有 `/api/*` 请求需带 `X-Auth-Token` 请求头（或 `?token=` 查询参数）。

## 实现要点

- **netsh 中文乱码**：所有命令经 `cmd /c "chcp 65001 >nul & netsh ..."` 强制 UTF-8 输出；`show` 解析按**空白分词取 4 列**（监听地址 监听端口 目标地址 目标端口），不依赖中英文表头与列宽
- **命令注入防护**：所有地址经 `IPAddress.TryParse` + 格式预检（拒绝纯数字、`%zone`），端口强类型
- **页面必达**：`index.html` 同时作为物理文件与**内嵌资源**，任何目录下启动都能打开页面；`/` 与 `/index.html` 有兜底路由
- **自重启**：控制台模式 `UseShellExecute=true` 拉起新实例后退出（不继承父进程 stdout 管道，子进程独立控制台）；服务模式 `sc stop/start`
- **服务路径解析**：用 `reg query ...\Services\<名> /v ImagePath` 读取（比 `sc qc` 输出更语言无关）

## 目录结构

```
PortProxyManager/
├── Program.cs             # Minimal API 入口 + 全部端点 + 鉴权中间件
├── src/
│   ├── Models.cs          # 规则 / 设置 / 校验器（模式推导）
│   ├── NetshService.cs    # netsh 执行与四模式解析
│   ├── ServiceManager.cs  # sc.exe 服务安装/卸载/启停
│   ├── CmdRunner.cs        # 统一命令执行（chcp 65001 UTF-8）
│   ├── RuleStore.cs       # rules.json / settings.json 持久化
│   ├── ReconcileService.cs # 期望 vs 实际比对补齐
│   ├── WatchdogService.cs  # 后台巡检
│   └── LogSink.cs         # 内存环形日志 + 按天落盘
├── wwwroot/index.html     # 单文件前端（无构建链）
└── publish/               # 发布产物（不入库）
```

运行时数据：`publish/data/rules.json`、`publish/data/settings.json`、`publish/logs/`。

## 已知限制

- 仅支持 Windows（依赖 netsh / sc）
- 链路本地 IPv6（`fe80::%zone`）不可用作转发地址（带 zone index 无法用于 netsh）
- 管理页无 HTTPS，令牌为明文传输——请仅在可信内网开启远程访问
- Windows 服务方式运行时数据目录跟随 exe 所在目录（`publish/data`）

## License

MIT
