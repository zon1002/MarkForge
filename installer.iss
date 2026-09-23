#define AppName      "MarkForge Markdown 工具組"
#define AppNameEn    "MarkForge"
#define AppVersion   "1.6.0"
#define AppPublisher "Fu Project"
#define EditorExe    "MarkForge.Editor.exe"
#define ViewerExe    "MarkForge.Viewer.exe"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppId={{4C6B7E2D-9A3F-4D8E-B6C1-7F2A5E9D0B31}
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
OutputDir=installer_output
OutputBaseFilename=MarkForge_Setup_v{#AppVersion}
SetupIconFile=MarkForge.Editor\Me.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64os
PrivilegesRequired=admin
UninstallDisplayIcon={app}\Editor\{#EditorExe}

; .NET 8 Desktop Runtime / WebView2 Runtime presence checks，缺少時可自動下載並靜默安裝
[Code]
var
  DepsDownloadPage: TDownloadWizardPage;
  NeedsDotNet8: Boolean;
  NeedsWebView2: Boolean;

function IsDotNet8Installed(): Boolean;
begin
  Result := DirExists(ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App'));
  if Result then
  begin
    Result := FileExists(ExpandConstant('{pf64}\dotnet\dotnet.exe'));
  end;
end;

function IsWebView2Installed(): Boolean;
begin
  Result := DirExists(ExpandConstant('{pf}\Microsoft\EdgeWebView\Application')) or
            DirExists(ExpandConstant('{pf32}\Microsoft\EdgeWebView\Application'));
end;

procedure InitializeWizard;
begin
  NeedsDotNet8 := not IsDotNet8Installed();
  NeedsWebView2 := not IsWebView2Installed();
  if NeedsDotNet8 or NeedsWebView2 then
    DepsDownloadPage := CreateDownloadPage('正在下載相依元件', '請稍候，下載完成後會自動安裝。', nil);
end;

// 缺少的相依元件（.NET 8 Desktop Runtime／WebView2 Runtime）會在使用者按下「安裝」前
// 詢問是否自動下載並靜默安裝（微軟官方常青連結，永遠指向可用的最新版本）；
// 下載或安裝失敗都只顯示提示訊息、不中斷本程式安裝，讓使用者可事後自行至官網補裝。
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  Msg: String;
  DotNetExe, WebView2Exe: String;
begin
  Result := True;
  if (CurPageID <> wpReady) or ((not NeedsDotNet8) and (not NeedsWebView2)) then
    exit;

  Msg := '偵測不到以下相依元件：' + #13#10;
  if NeedsDotNet8 then
    Msg := Msg + '- .NET 8 Desktop Runtime (x64)' + #13#10;
  if NeedsWebView2 then
    Msg := Msg + '- Microsoft Edge WebView2 Runtime（即時預覽功能需要）' + #13#10;
  Msg := Msg + #13#10 + '是否要現在自動下載並安裝（需連線至 microsoft.com）？' + #13#10 +
    '選「否」會略過，安裝完成後請自行下載安裝，否則相關功能可能無法使用。';

  if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDNO then
    exit;

  DepsDownloadPage.Clear;
  if NeedsDotNet8 then
    DepsDownloadPage.Add('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
      'windowsdesktop-runtime-8.0-win-x64.exe', '');
  if NeedsWebView2 then
    DepsDownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703',
      'MicrosoftEdgeWebview2Setup.exe', '');
  DepsDownloadPage.Show;
  try
    DepsDownloadPage.Download;
  except
    DepsDownloadPage.Hide;
    if DepsDownloadPage.AbortedByUser then
      Log('使用者取消下載相依元件。')
    else
      SuppressibleMsgBox('下載相依元件失敗：' + GetExceptionMessage + #13#10 +
        '請安裝完成後自行至官網下載安裝缺少的元件。', mbError, MB_OK, IDOK);
    exit;
  end;
  DepsDownloadPage.Hide;

  if NeedsDotNet8 then
  begin
    DotNetExe := ExpandConstant('{tmp}\windowsdesktop-runtime-8.0-win-x64.exe');
    if not Exec(DotNetExe, '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      SuppressibleMsgBox('無法啟動 .NET 8 Desktop Runtime 安裝程式，請稍後自行至 ' +
        'https://dotnet.microsoft.com/download/dotnet/8.0 下載安裝。', mbError, MB_OK, IDOK)
    else if (ResultCode <> 0) and (ResultCode <> 3010) then
      SuppressibleMsgBox('.NET 8 Desktop Runtime 安裝可能未成功完成（結束碼 ' + IntToStr(ResultCode) + '）。',
        mbError, MB_OK, IDOK);
  end;

  if NeedsWebView2 then
  begin
    WebView2Exe := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    if not Exec(WebView2Exe, '/silent /install', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      SuppressibleMsgBox('無法啟動 WebView2 Runtime 安裝程式，請稍後自行至 ' +
        'https://developer.microsoft.com/microsoft-edge/webview2/ 下載安裝。', mbError, MB_OK, IDOK)
    else if ResultCode <> 0 then
      SuppressibleMsgBox('WebView2 Runtime 安裝可能未成功完成（結束碼 ' + IntToStr(ResultCode) + '）。',
        mbError, MB_OK, IDOK);
  end;
end;

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; GroupDescription: "其他選項:"

[Files]
Source: "publish\Editor\*"; DestDir: "{app}\Editor"; Flags: ignoreversion recursesubdirs
Source: "publish\Viewer\*"; DestDir: "{app}\Viewer"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\MarkForge Editor";   Filename: "{app}\Editor\{#EditorExe}"
Name: "{group}\MarkForge Viewer";   Filename: "{app}\Viewer\{#ViewerExe}"
Name: "{group}\解除安裝";           Filename: "{uninstallexe}"
Name: "{commondesktop}\MarkForge Editor"; Filename: "{app}\Editor\{#EditorExe}"; Tasks: desktopicon
Name: "{commondesktop}\MarkForge Viewer"; Filename: "{app}\Viewer\{#ViewerExe}"; Tasks: desktopicon

; 檔案關聯：.md / .markdown 預設以 MarkForge Viewer 開啟，
; 右鍵選單另外加一個「以 MarkForge Editor 開啟」的項目
[Registry]
Root: HKCR; Subkey: ".md"; ValueType: string; ValueName: ""; ValueData: "MarkForge.MarkdownFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".markdown"; ValueType: string; ValueName: ""; ValueData: "MarkForge.MarkdownFile"; Flags: uninsdeletevalue

Root: HKCR; Subkey: "MarkForge.MarkdownFile"; ValueType: string; ValueName: ""; ValueData: "Markdown 文件"; Flags: uninsdeletekey
Root: HKCR; Subkey: "MarkForge.MarkdownFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Viewer\{#ViewerExe},0"

Root: HKCR; Subkey: "MarkForge.MarkdownFile\shell\open"; ValueType: string; ValueName: ""; ValueData: "以 &MarkForge Viewer 開啟"
Root: HKCR; Subkey: "MarkForge.MarkdownFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Viewer\{#ViewerExe}"" ""%1"""

Root: HKCR; Subkey: "MarkForge.MarkdownFile\shell\editwithmarkforge"; ValueType: string; ValueName: ""; ValueData: "以 Mark&Forge Editor 開啟"
Root: HKCR; Subkey: "MarkForge.MarkdownFile\shell\editwithmarkforge"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Editor\{#EditorExe},0"
Root: HKCR; Subkey: "MarkForge.MarkdownFile\shell\editwithmarkforge\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Editor\{#EditorExe}"" ""%1"""

[Run]
Filename: "{app}\Editor\{#EditorExe}"; Description: "立即啟動 MarkForge Editor"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
