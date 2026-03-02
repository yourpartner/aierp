# Claude Code CLI 安装 + 手机 Remote 控制

按顺序做下面步骤即可。

---

## 一、安装前准备（Windows）

### 1. 安装 Node.js（18 或以上）

1. 打开 https://nodejs.org ，下载并安装 **LTS** 版本。
2. 安装完成后，**新开一个 PowerShell**，执行：
   ```powershell
   node -v
   ```
   应显示类似 `v20.x.x`。若提示找不到命令，把 Node 安装路径加入系统环境变量 **Path**。

### 2. 安装 Git

1. 打开 https://git-scm.com/download/win ，下载并安装。
2. 新开 PowerShell，执行：
   ```powershell
   git --version
   ```
   能打出版本号即可。

---

## 二、安装 Claude Code CLI

1. 打开 **PowerShell**（或 CMD）。
2. 执行：
   ```powershell
   npm install -g @anthropic-ai/claude-code
   ```
3. 等待安装结束。
4. 检查是否成功：
   ```powershell
   claude --version
   ```
   若显示版本号（如 `claude-code 0.x.x`）即安装成功。

**若提示“找不到 claude 命令”**：  
- 执行 `npm config get prefix`，记下输出路径（例如 `C:\Users\你的用户名\AppData\Roaming\npm`）。  
- 右键“此电脑” → 属性 → 高级系统设置 → 环境变量 → 系统变量里选中 **Path** → 编辑 → 新建，把上面路径和该路径下的 `node_modules` 都加进去（若已有则不用重复）。  
- 关闭并重新打开 PowerShell，再执行 `claude --version`。

---

## 三、首次使用：信任工作区 + 登录

1. 进入你的项目目录：
   ```powershell
   cd d:\yanxia
   ```
2. 启动 Claude Code：
   ```powershell
   claude
   ```
3. **工作区信任**：若弹出是否信任此工作区，输入同意（如 `y` 或按提示操作）。
4. **登录**（必须，否则无法用手机 Remote）：
   - 在 Claude 对话里输入：
     ```
     /login
     ```
   - 按提示在浏览器中打开链接，用 **claude.ai** 账号登录（需 **Pro 或 Max** 订阅）。
   - 登录成功后回到终端，会话会显示已登录。

关掉终端或输入退出命令即可结束这次会话。下次直接用 `claude` 或 `claude remote-control` 即可。

---

## 四、电脑上开启“手机可接入”的会话（Remote Control）

每次你想在手机上接着用时，在**电脑**上这样做：

1. 打开 PowerShell，进入项目目录：
   ```powershell
   cd d:\yanxia
   ```
2. 启动“可被手机连接”的会话：
   ```powershell
   claude remote-control
   ```
3. 终端会保持运行，并显示：
   - **一段 Session URL**（形如 `https://claude.ai/code/...`）
   - 提示：**按空格键可显示/隐藏 QR 码**

**注意**：这个终端窗口要**保持打开**，关掉或结束进程，手机就断连。需要时重新执行上面两步即可。

---

## 五、在手机上连接

### 方式 A：用 Claude 官方 App（推荐）

1. 在手机应用商店搜索 **Claude**（Anthropic 出品），安装 **Claude** App。
2. 打开 App，用**和电脑上 /login 相同的 claude.ai 账号**登录。
3. 连接方式二选一：
   - **扫码**：在电脑终端里按**空格**调出 QR 码，用 Claude App 里的“扫码”扫该 QR 码；或  
   - **会话列表**：在 App 里找到会话列表，看到带 **电脑图标 + 绿点** 的会话，点进去即为本机的 Remote 会话。

连接成功后，手机上的对话和电脑上是**同一条会话**，你在手机上的输入会同步到电脑，电脑上的 Claude 在本地执行（改代码、跑测试等），结果会显示在手机和电脑上。

### 方式 B：用手机浏览器

1. 在电脑终端里**复制 Session URL**（`claude remote-control` 启动时显示的那条链接）。
2. 在手机浏览器中打开该链接，会跳到 **claude.ai/code** 并进入同一会话。
3. 用同一 claude.ai 账号登录（若未登录）。

---

## 六、日常使用流程小结

| 场景           | 你在电脑上做                         | 你在手机上做                         |
|----------------|--------------------------------------|--------------------------------------|
| 在电脑上开发   | `cd d:\yanxia` → `claude` 或 `claude remote-control`，和 Claude 对话 | 不需要                               |
| 想用手机接着用 | 先运行 `claude remote-control`，保持终端开着 | 打开 Claude App 或浏览器，扫码/点链接进入同一会话 |
| 用手机发指令   | 终端保持运行即可                     | 在 App/网页里输入需求，Claude 在电脑上执行 |

---

## 七、常见问题

- **手机找不到“电脑图标”的会话**  
  确认电脑上已执行 `claude remote-control` 且终端没关；用同一 claude.ai 账号登录 App；可尝试扫码或直接打开终端里显示的 Session URL。

- **Remote 要求 Pro/Max**  
  手机 Remote 仅支持 claude.ai 的 **Pro** 或 **Max** 订阅；Team/Enterprise 或仅 API 不行。用 `/login` 登录的是 claude.ai 账号即可。

- **关掉电脑终端后手机断连**  
  属于正常。需要再用手机时，在电脑上重新执行 `cd d:\yanxia` 和 `claude remote-control`，再在手机上重新连接该会话（或新会话）。

- **已经在和 Claude 对话，临时想开 Remote**  
  在**当前会话**里输入：
  ```
  /remote-control
  ```
  或简写 `/rc`，会显示 Session URL 和 QR 码，手机即可接入当前对话。

按上面顺序做完，你就完成了 **CLI 安装 + 手机 Remote 控制** 的配置。
