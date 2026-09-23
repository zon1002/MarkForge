# MarkForge Markdown 工具組

**版本：v1.5.1**

Windows 桌面用的 Markdown 編輯／檢視工具組，內建即時預覽、Mermaid 圖表渲染，以 .NET 8 WinForms 打造，包含 **MarkForge Editor**（編輯器）與 **MarkForge Viewer**（檢視器）兩支獨立程式。

---

## 功能

### MarkForge Editor（編輯器）

- **Scintilla 編輯核心**：內建 Markdown 語法上色（標題、粗體、斜體、清單、引用、程式碼、連結等），支援復原／重做
- **即時 WebView2 預覽**：邊打字邊看渲染結果（250ms 防抖動），支援 CommonMark + GFM（表格、任務清單、刪除線、emoji、自動連結等，由 Markdig 解析）
- **編輯區／預覽區雙向同步捲動**：捲動任一邊，另一邊自動跟上。以標題（`#`～`######`）與程式碼／Mermaid 區塊為錨點定位，而非單純比例換算，即使原始碼與渲染結果高度差異很大（例如幾行 Mermaid 語法渲染成整頁的圖）也能正確對齊；捲動經過大型區塊時會先釘住在區塊最前端，捲完整塊才前進到下一個錨點
- **Mermaid 圖表縮放／平移**：滑鼠滾輪縮放（游標錨定，20%～1200%，不需按 Ctrl）、左鍵拖曳平移、雙擊重設
- **格式化工具列**：粗體、斜體、刪除線、標題（H1～H6）、引用、項目清單、編號清單、工作清單（打勾／打叉／三角形／驚嘆號四種標記）、行內程式碼、程式碼區塊、連結、圖片、表格、水平分隔線
- **顯示／隱藏窗格**：「檢視」選單可獨立切換編輯器與預覽窗格的顯示，兩者可任意組合，但不允許同時全部隱藏
- **深色／淺色主題**：一鍵切換編輯器與預覽的配色
- **字型自訂**：編輯器字型與預覽字型可分別設定
- **預覽寬度可調整**：「檢視」選單可選擇 860px（預設）／1000px／1200px／1400px／不限制寬度，或「自訂寬度...」輸入任意數值，內容一律置中顯示；同一設定也會套用到匯出的 HTML
- **檔案操作**：新增、開啟、儲存、另存新檔，拖放檔案至視窗即可開啟
- **匯出**：
  - 匯出為 HTML——完全獨立的單一 HTML 檔案（CSS／JS 全部內嵌），可脫離 MarkForge 在任何瀏覽器開啟
  - 另存為 PDF——直接用目前的預覽畫面轉存
- **狀態列**：即時顯示游標行列位置、字數統計、目前檔名與未儲存標記

### MarkForge Viewer（檢視器）

- **唯讀快速檢視**：不含編輯功能，開啟即顯示渲染結果，適合搭配外部編輯器當「第二螢幕」預覽
- **自動重新載入**：監看檔案異動（`FileSystemWatcher`），檔案被其他程式修改後自動重新整理
- **Mermaid 圖表縮放／平移**：與 Editor 相同的滾輪縮放、拖曳平移、雙擊重設
- **深色／淺色主題**、**預覽字型自訂**
- **預覽寬度可調整**：工具列「寬度」下拉選單，選項與 Editor 相同（860px／1000px／1200px／1400px／不限制寬度／自訂寬度）
- **另存為 PDF**
- **拖放開啟檔案**、工具列可隨時「開啟」其他檔案

### 快捷鍵

| 按鍵 | Editor | Viewer |
|------|--------|--------|
| `Ctrl+N` | 新增文件 | — |
| `Ctrl+O` | 開啟檔案 | 開啟檔案 |
| `Ctrl+S` | 儲存 | — |
| `Ctrl+Shift+S` | 另存新檔 | — |
| `Ctrl+Z` / `Ctrl+Y` | 復原／重做 | — |
| `Ctrl+X` / `Ctrl+C` / `Ctrl+V` | 剪下／複製／貼上 | — |
| `Ctrl+B` / `Ctrl+I` | 粗體／斜體 | — |
| `Ctrl+K` | 插入連結 | — |
| `F5` | — | 重新整理 |

---

## 輸入檔案格式

| 副檔名 | 說明 |
|--------|------|
| `.md` | Markdown 文件 |
| `.markdown` | Markdown 文件（同 `.md`） |

安裝程式會將這兩種副檔名預設關聯到 MarkForge Viewer；在檔案總管右鍵可另外選擇「以 MarkForge Editor 開啟」。

---

## 輸出檔案

| 類型 | 說明 |
|------|------|
| `.md` / `.markdown` | Editor 儲存／另存新檔的原始 Markdown 檔案 |
| `.html` | Editor「匯出為 HTML」——單一檔案、CSS／JS 全內嵌、可獨立開啟 |
| `.pdf` | Editor／Viewer「另存為 PDF」——依目前預覽畫面轉存 |

---

## 操作步驟

**編輯並即時預覽（Editor）**
1. 開啟 MarkForge Editor，**「開啟...」**選擇既有 `.md` 檔案，或直接**「新增」**
2. 左側編輯區輸入 Markdown，右側預覽窗格即時顯示渲染結果
3. 用工具列或快捷鍵套用格式（粗體、標題、清單等）
4. 需要單獨查看某一側時，「檢視」選單切換**「顯示編輯器」／「顯示預覽」**
5. 完成後 `Ctrl+S` 儲存，或用「檔案」選單**「匯出為 HTML」／「另存為 PDF」**輸出

**單純檢視（Viewer）**
1. 開啟 MarkForge Viewer，**「開啟...」**選擇 `.md` 檔案，或用檔案總管的右鍵選單直接開啟
2. 用其他編輯器（含 MarkForge Editor）修改該檔案時，Viewer 會自動偵測變更並重新整理畫面
3. 圖表看不清楚時，在 Mermaid 圖表上滾動滑鼠滾輪縮放、拖曳平移，雙擊還原

---

## 系統需求

- Windows 10 / 11（x64）
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）—— 安裝程式會自動偵測，缺少時可一鍵下載安裝
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) —— 即時預覽功能需要，安裝程式同樣會自動偵測並安裝

---

## 建置說明（開發者）

### 前置需求

| 工具 | 版本 |
|------|------|
| .NET SDK | 8.0 以上 |
| Inno Setup | 6（僅打包安裝程式需要） |

### 相依套件

```
MarkForge.Core     Markdig 1.3.2
MarkForge.Editor   Microsoft.Web.WebView2、Scintilla.NET 5.3.2.9、Svg 3.4.8
MarkForge.Viewer   Microsoft.Web.WebView2
```

highlight.js、mermaid.js、GitHub 風格 CSS 皆以 embedded resource 形式打包進 MarkForge.Core，執行期不連外部 CDN。

### 建置步驟

```bash
# 還原並建置
dotnet build MarkForge.Editor/MarkForge.Editor.csproj -c Release
dotnet build MarkForge.Viewer/MarkForge.Viewer.csproj -c Release

# 發布（framework-dependent, win-x64）
dotnet publish MarkForge.Editor/MarkForge.Editor.csproj -c Release -r win-x64 --self-contained false -o publish/Editor
dotnet publish MarkForge.Viewer/MarkForge.Viewer.csproj -c Release -r win-x64 --self-contained false -o publish/Viewer

# 打包安裝程式（需先完成上面的 publish）
iscc installer.iss
```

### 專案結構

```
MarkForge_v1.1/
├── MarkForge.Core/           # 共用核心：Markdown 渲染、HTML 模板
│   ├── MarkdownRenderer.cs   # Markdig pipeline 封裝
│   ├── HtmlTemplate.cs       # 預覽/匯出用的 HTML 模板與縮放/同步捲動 JS
│   ├── MermaidExtension.cs   # ```mermaid 區塊渲染 + 原始碼行號標記
│   ├── SourceSyncExtension.cs# 標題原始碼行號標記（同步捲動用）
│   └── Assets/                # highlight.js、mermaid.js、GitHub CSS
├── MarkForge.Editor/          # 編輯器（WinForms + Scintilla + WebView2）
├── MarkForge.Viewer/          # 檢視器（WinForms + WebView2）
├── installer.iss              # Inno Setup 安裝程式腳本
├── versions.md                 # 版本說明
└── MarkForge.slnx
```

### 技術架構

| 元件 | 技術 |
|------|------|
| GUI 框架 | .NET 8 WinForms |
| Markdown 解析 | Markdig（CommonMark + GFM 擴充） |
| 程式碼編輯 | Scintilla.NET |
| 預覽渲染 | Microsoft Edge WebView2 |
| 圖表渲染 | Mermaid.js（離線內嵌） |
| 語法上色（預覽） | highlight.js（離線內嵌） |
| 安裝程式 | Inno Setup 6 |

---

## 版本歷程

詳見 [versions.md](versions.md)。
