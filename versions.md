# 版本說明 (Versions)

所有版本依 [語義化版本](https://semver.org/lang/zh-TW/) 命名：`主版號.次版號.修訂號`

---

## [v1.5.1] — 2026-08-28

### 新增功能

- **預覽寬度新增「自訂寬度...」選項**：v1.5.0 的預設寬度（860／1000／1200／1400px／不限制寬度）之外，Editor「檢視」選單與 Viewer 工具列的寬度選單都加了「自訂寬度...」，跳出小對話框可輸入任意數值（320～4000px）。不論選預設還是自訂寬度，內容一律置中顯示（`margin: 0 auto`）；「不限制寬度」維持原樣（撐滿整個窗格，無置中意義）。

---

## [v1.5.0] — 2026-08-27

### 新增功能

- **預覽寬度可調整**：預覽區的閱讀版面寬度原本寫死 860px（模仿 GitHub 的置中閱讀版面）。
  - Editor「檢視」選單新增「預覽寬度」子選單，Viewer 工具列新增「寬度」下拉按鈕，皆提供 860px（預設）／1000px／1200px／1400px／不限制寬度五種預設選項，選「不限制寬度」時內容會撐滿整個預覽窗格
  - 此設定也會套用到 Editor「匯出為 HTML」的輸出

### 修正

- **Viewer 開啟含 Mermaid 圖表的檔案有機率再次跳出「無法初始化預覽元件」**：加入「匯出為 HTML」的內嵌版本（v1.4.1）時，`MarkdownRenderer.RenderDocument()` 被誤改成呼叫內嵌版本的 `HtmlTemplate.ExportStandalone()`，但 Viewer 的一般渲染路徑也共用這個方法且是透過 `NavigateToString` 顯示——等於把 3.7MB 的 mermaid.js／highlight.js 又整包塞回 2MB 限制的 API，重新引入 v1.0.0 那個崩潰問題。拆成兩個方法：`RenderDocument()`（走虛擬主機，給 Viewer／即時預覽用）與 `RenderStandaloneDocument()`（內嵌組資源，只給存檔匯出用），兩者用途分開後不再互相影響。

---

## [v1.4.1] — 2026-08-27

### 修正

- **「匯出為 HTML」產生的檔案脫離 App 就無法正常顯示**：v1.0.1 為了解決 WebView2 `NavigateToString` 2MB 限制，把即時預覽用的 CSS/JS 資源改成透過 `SetVirtualHostNameToFolderMapping` 虛擬主機（`https://markforge.assets/...`）載入，但「匯出為 HTML」誤用了同一套模板，導致匯出的檔案也引用這個只在 App 內部有效的網址——單獨用瀏覽器開啟時 Mermaid 圖表與語法上色都會失效。
  - 新增 `HtmlTemplate.ExportStandalone()`，專供檔案匯出使用：CSS/JS 直接內嵌進 HTML（不經過 `NavigateToString`，不受 2MB 限制影響），匯出的檔案完全獨立、可在任何瀏覽器單獨開啟

---

## [v1.4.0] — 2026-08-27

### 變更

- **同步捲動改用標題/程式碼區塊錨點定位，取代單一比例捲動**：原本用「編輯區目前在整份文件的第幾 % 」直接換算成預覽區的捲動百分比，一旦文件混雜標題、長 Mermaid 圖、程式碼區塊就必然失準（原始碼幾行的 Mermaid 語法可能渲染成幾百 px 高的圖）。
  - 新增 `SourceSyncExtension`：為每個標題（`#`～`######`）標上原始碼行號
  - `MermaidExtension` 同步為 Mermaid 圖與一般程式碼區塊標上原始碼起訖行號
  - 預覽頁面用這些標記建立「錨點表」（原始碼行號 ↔ 渲染後像素位置），捲動時在錨點之間線性內插；標題之間平滑內插，Mermaid 圖／程式碼區塊視為「整塊」——捲動位置落在區塊內時釘住在區塊最前端，捲過整個區塊才前進到下一個錨點

### 修正

- **開啟檔案時編輯區跳到檔案最後一行**：`Scintilla.Text = ...` 賦值後游標預設落在文件結尾，導致開檔案時畫面顯示檔案尾端而非開頭；同步捲動上線後這個既有小毛病連帶把預覽區也拖到底部，一併修正為開檔後回到第一行。
- **開檔案後頭幾秒編輯區/預覽區會自己捲動位置**：Scintilla 對長文件的自動換行與語法上色是背景延遲處理，處理過程中會觸發假的捲動事件，經同步捲動機制放大成畫面明顯位移。新增「安定視窗」機制：捲動事件持續出現時延後啟用同步，直到真正靜止一段時間才開始追蹤。
- **Chromium 自動捲動錨定（scroll anchoring）與同步捲動互相干擾**：Mermaid 圖非同步渲染、頁面高度變化時，瀏覽器會自動調整 `scrollY` 以維持錨定內容在畫面中，這個調整又被同步捲動誤判為使用者操作而回傳給編輯區，造成漂移。加入 `overflow-anchor: none` 停用瀏覽器的自動捲動錨定。

---

## [v1.3.0] — 2026-08-27

### 新增功能

- **Editor 編輯區／預覽區雙向同步捲動**：捲動編輯區時預覽區跟著捲動，反之亦然，兩側皆有防迴圈旗標避免互相帶動造成抖動或死循環。
  - 編輯區 → 預覽區：透過 Scintilla 的 `UpdateUI`（`VScroll`）事件換算捲動比例，用 `ExecuteScriptAsync` 呼叫預覽頁面 JS 同步捲動
  - 預覽區 → 編輯區：預覽頁面監聽 `scroll` 事件，透過 `window.chrome.webview.postMessage` 回傳捲動比例給 C#，換算回 Scintilla 的 `FirstVisibleLine`

---

## [v1.2.0] — 2026-08-27

### 新增功能

- **Editor 顯示／隱藏編輯器與預覽窗格**：「檢視」選單新增「顯示編輯器」「顯示預覽」兩個獨立開關，可任意組合（兩者皆顯示、只留編輯器、只留預覽），唯一擋住的情況是兩者同時隱藏——會跳出提示，不允許畫面上完全沒有可視窗格。

---

## [v1.1.0] — 2026-08-24

### 新增功能

- **Mermaid 圖表縮放／平移**：每個 Mermaid 圖表獨立支援游標錨定的滾輪縮放（20%～1200%，不需按 Ctrl）與左鍵拖曳平移，雙擊重設回原始大小。取代先前依賴瀏覽器整頁縮放（Ctrl+滾輪）造成的「只能整頁縮放、無法左右平移、Editor 打字時定位亂跑」等問題。

---

## [v1.0.1] — 2026-08-24

### 修正

- **Editor／Viewer 啟動後跳出「無法初始化預覽元件」**：根因是 `mermaid.min.js`（3.57 MB）被整包內嵌進每次渲染的 HTML 字串中，超過 WebView2 `NavigateToString` 的 2 MB 內容大小上限，導致連空白預覽頁都會拋出 `ArgumentException`。
  - 改用 `CoreWebView2.SetVirtualHostNameToFolderMapping` 將 highlight.js、mermaid.js、CSS 等資源解壓縮到 `%TEMP%\MarkForge\<App>\WebAssets` 並以外部連結（`<script src>`／`<link>`）引用，不再內嵌進 HTML 字串

---

## [v1.0.0] — 2026-08-24

### 新增功能

- **首次建置與發布**：安裝 .NET 8 SDK，建置 MarkForge.Core／MarkForge.Editor／MarkForge.Viewer 三個專案，以 framework-dependent、win-x64 方式發布，並透過 Inno Setup 6 打包成 `MarkForge_Setup_v1.0.0.exe` 安裝程式（含 .NET 8 Desktop Runtime／WebView2 Runtime 自動偵測與下載安裝）。

---

## 功能演進脈絡

```
v1.0.0  + 首次建置、發布、打包安裝程式
        │
v1.0.1  + 修正預覽初始化失敗（WebView2 2MB 限制）
        │
v1.1.0  + Mermaid 圖表縮放／平移
        │
v1.2.0  + Editor 顯示／隱藏編輯器與預覽窗格
        │
v1.3.0  + Editor 編輯區／預覽區雙向同步捲動
        │
v1.4.0  + 同步捲動改用標題／程式碼區塊錨點定位
        + 修正開檔捲動位置與捲動漂移問題
        │
v1.4.1  + 修正「匯出為 HTML」脫離 App 無法正常顯示
        │
v1.5.0  + 預覽寬度可調整（Editor + Viewer）
        + 修正 Viewer 渲染路徑誤用內嵌版本模板
        │
v1.5.1  + 預覽寬度新增「自訂寬度...」選項
```
