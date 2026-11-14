' The one version of LTFSCopyGui2 and LTFSCatalog (both projects link this file; Installer\LCG.iss reads the Number line).
' Every version pushed to GitHub adds 0.001 here: 1.001, 1.002, 1.003 ... and gets a line in docs\VERSIONS.md.
Imports System.Reflection

<Assembly: AssemblyVersion(AppVersion.Number)>
<Assembly: AssemblyFileVersion(AppVersion.Number)>
<Assembly: AssemblyInformationalVersion(AppVersion.Number)>

Friend Module AppVersion
    Public Const Number As String = "1.005"
    ''' <summary>The application's name (window titles, the LTFS index creator, the tape's MAM application name).
    ''' The exe stays LTFSCopyGUI.exe.</summary>
    Public Const CopyGuiName As String = "LTFSCopyGui2"
End Module
