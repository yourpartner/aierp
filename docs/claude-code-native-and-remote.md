# 用原生 Claude Code + 手机 Remote 开发本项目的指南

## 一、为什么用「原生」Claude Code

- **上下文管理不同**：Claude Code 是代码库级、Git 感知、自动全局扫描，按需拉取相关文件；Cursor 是索引 + 检索，上下文范围和策略不同。原生方式以 Claude Code 的上下文模型为主。
- **手机 Remote**：官方提供 Remote Control，用手机/平板/浏览器接入**同一台电脑上**正在运行的 Claude Code 会话，代码和 MCP 都在本机，手机只是「遥控器」。

---

## 二、前置条件（必须满足）

1. **订阅**：Claude **Pro 或 Max**（Team/Enterprise 不支持 Remote Control）。
2. **登录**：用 claude.ai 账号在 Claude Code 里登录（不能用纯 API Key）。
3. **本机安装 Claude Code CLI**（见下一节）。
4. **工作区信任**：在项目目录里至少运行过一次 `claude` 并接受工作区信任。

---

## 三、安装 Claude Code（Windows 本机）

在 **PowerShell** 或 **CMD** 中执行（需已安装 Node.js 18+ 和 Git）：

```bash
npm install -g @anthropic-ai/claude-code
```

安装后终端可用 `claude` 命令。若提示找不到命令，检查 npm 全局路径是否在 PATH 中。

---

## 四、原生使用方式：以 CLI 为主

1. **进入项目目录**
   ```bash
   cd d:\yanxia
   ```

2. **首次使用：信任工作区 + 登录**
   ```bash
   claude
   ```
   - 若弹出工作区信任，选「信任」。
   - 若未登录，在 Claude Code 里输入：`/login`，按提示用 claude.ai 完成登录。

3. **日常开发**
   - 在同一目录运行 `claude`，直接对话布置任务（改功能、修 bug、重构等）。
   - Claude Code 会自己读仓库、跑命令、改文件；需要看 diff 或想在编辑器里改时，可输入 `/ide` 关联到 Cursor 或 VS Code。

这样就是以「原生 Claude Code」为主、编辑器为辅的用法，上下文由 Claude Code 管理。

---

## 五、手机 Remote：用官方 Remote Control

### 5.1 在本机开启 Remote Control

**方式 A：直接开一个可被远程连接的会话**
```bash
cd d:\yanxia
claude remote-control
```
终端会保持运行，并显示：
- **Session URL**：在手机浏览器打开 claude.ai/code 时可用。
- **按空格**：显示 **QR 码**，用 Claude 手机 App 扫码即可接入。

**方式 B：已经在和 Claude 对话，想改成可远程**
在 Claude Code 对话里输入：
```
/remote-control
```
或简写：`/rc`
当前会话会变成可远程接入，并显示 Session URL 和 QR 码。

### 5.2 在手机上连接

- **Claude App（iOS/Android）**  
  - 打开 App → 在会话列表里找到带「电脑图标 + 绿点」的会话（即本机的 Remote Control 会话），或  
  - 直接扫终端里显示的 **QR 码** 进入该会话。

- **浏览器**  
  在手机浏览器打开终端里显示的 **Session URL**，会跳到 claude.ai/code 并进入同一会话。

连接后：
- 对话、发指令、审批编辑都在手机完成；
- 代码和执行环境仍在你的电脑上，手机只是界面；
- 电脑休眠或断网后恢复，会话会自动重连（约 10 分钟内）。

### 5.3 可选：所有会话默认可远程

在 Claude Code 里执行 `/config`，将 **Enable Remote Control for all sessions** 设为 `true`，之后每个新会话都会自动支持手机/浏览器接入。

---

## 六、和 Cursor 的配合（可选）

- 若你**主要用原生 Claude Code**，就平时在 `d:\yanxia` 下用 `claude` 或 `claude remote-control` 开发。
- 当需要**在 Cursor 里看 diff、手动微调**时，在 Claude Code 里输入 `/ide`，选择 Cursor，之后 Claude 的编辑会同步到 Cursor 里以 diff 形式呈现。

这样既保留「原生 Claude Code 的上下文与流程」，又能在需要时用 Cursor 做可视化编辑。

---

## 七、注意事项（官方限制）

- **Pro/Max**：Remote Control 仅支持 Pro 或 Max，且需通过 claude.ai 登录。
- **终端不能关**：`claude remote-control` 是本地进程，关掉终端或结束进程，远程会话就断；需要时重新运行 `claude remote-control` 即可。
- **长时间断网**：机器在线但约 10 分钟无法连网，会话会超时退出，重新执行 `claude remote-control` 开新会话。
- **单会话单远程**：一个 Claude Code 会话同一时间只支持一个远程连接；多开多个 `claude`/`claude remote-control` 可有多会话。

---

## 八、没有 Claude App 时

在 Claude Code 里输入 `/mobile`，会显示下载 Claude App 的 QR 码（iOS/Android），扫码安装后再按上面步骤连接。

---

以上完成后，你就实现了：**用原生 Claude Code 管理上下文 + 本机开发 + 手机 Remote 接入继续开发**。若你告诉我当前是「已装 CLI / 未装」和「是否已有 Pro/Max」，可以再帮你精简成「下一步只做哪几件事」的清单。
