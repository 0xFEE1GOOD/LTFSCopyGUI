#define MyAppName "LTFSCopyGui2"
#define MyAppPublisher "Nullpinter"
#define MyAppExeName "LTFSCopyGUI.exe"

; Read the version (shared with LTFSCatalog) from Common\AppVersion.vb while compiling the installer:
; the line is   Public Const Number As String = "1.001"
#define VersionFile FileOpen("..\Common\AppVersion.vb")
#if !VersionFile
  #error "Unable to open Common\AppVersion.vb"
#endif
#define VersionMarker "Public Const Number As String = """
#define VersionLine ""
#define ReadVersionLine
#for {VersionLine = FileRead(VersionFile); Pos(VersionMarker, VersionLine) == 0 && !FileEof(VersionFile); VersionLine = FileRead(VersionFile)} ReadVersionLine
#expr FileClose(VersionFile)
#if Pos(VersionMarker, VersionLine) == 0
  #error "Unable to find the version Number in Common\AppVersion.vb"
#endif
#define MyAppVersion Copy(VersionLine, Pos(VersionMarker, VersionLine) + Len(VersionMarker), Len(VersionLine) - Pos(VersionMarker, VersionLine) - Len(VersionMarker))

[Setup]
; Own AppId: installs beside upstream LTFSCopyGUI instead of upgrading it.
AppId={{587A2853-343C-4D13-A421-B0AF3BE58CDA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName=C:\{#MyAppName}
OutputDir=.
OutputBaseFilename={#MyAppName}-{#MyAppVersion}
SetupIconFile=Koko.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic windows11 includetitlebar
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.OverwriteOptionalFiles=覆盖已有可选文件
english.OverwriteOptionalFiles=Overwrite existing optional files
chinesesimp.OptionalFeatures=可选功能
english.OptionalFeatures=Optional features
chinesesimp.UsePsExec=使用 PsExec（不知道用来干什么就不要开启）
english.UsePsExec=Use PsExec(Don't enable it if you don't know why)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "overwriteoptional"; Description: "{cm:OverwriteOptionalFiles}"; Flags: unchecked
Name: "usepsexec"; Description: "{cm:UsePsExec}"; GroupDescription: "{cm:OptionalFeatures}"; Flags: unchecked

[Files]
; 主程序构建产物始终更新，并保持 Release 目录的子目录结构。
Source: "..\LTFSCopyGUI\bin\x64\Release\*"; DestDir: "{app}"; Excludes: "\config\*,\log\*,\logpages\*,\schema\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; deploy 中的文件属于可选文件。默认只安装目标位置中不存在的文件；
; 选中“覆盖已有可选文件”后才覆盖同名文件。
Source: "deploy\*"; DestDir: "{app}"; Excludes: "0_PsExec64.exe"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: OverwriteOptionalFiles
Source: "deploy\*"; DestDir: "{app}"; Excludes: "0_PsExec64.exe"; Flags: ignoreversion onlyifdoesntexist recursesubdirs createallsubdirs; Check: not OverwriteOptionalFiles

; 启用 PsExec 时去掉禁用前缀，否则保留原文件名。
Source: "deploy\0_PsExec64.exe"; DestDir: "{app}"; DestName: "PsExec64.exe"; Flags: ignoreversion; Tasks: usepsexec; Check: OverwriteOptionalFiles
Source: "deploy\0_PsExec64.exe"; DestDir: "{app}"; DestName: "PsExec64.exe"; Flags: ignoreversion onlyifdoesntexist; Tasks: usepsexec; Check: not OverwriteOptionalFiles
Source: "deploy\0_PsExec64.exe"; DestDir: "{app}"; Flags: ignoreversion; Tasks: not usepsexec; Check: OverwriteOptionalFiles
Source: "deploy\0_PsExec64.exe"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist; Tasks: not usepsexec; Check: not OverwriteOptionalFiles

[InstallDelete]
; 从已启用 PsExec 的旧安装升级时，未启用该选项则移除旧的启用文件。
Type: files; Name: "{app}\PsExec64.exe"; Tasks: not usepsexec

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function OverwriteOptionalFiles: Boolean;
begin
  Result := WizardIsTaskSelected('overwriteoptional');
end;
