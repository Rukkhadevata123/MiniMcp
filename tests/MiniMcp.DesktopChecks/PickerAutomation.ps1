param([long]$OwnerWindow, [string]$ValuePath, [string]$FixtureRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\') + '\'
$taskValue = [IO.Path]::GetFullPath($ValuePath)
if (!$taskValue.StartsWith($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Picker target must stay inside the test fixture' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class PickerWindows {
    private delegate bool Visitor(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr parent, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, StringBuilder text);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static IntPtr[] Owned(long owner) {
        var result = new List<IntPtr>();
        uint ownerProcess; GetWindowThreadProcessId(new IntPtr(owner), out ownerProcess);
        EnumWindows((window, _) => {
            if (window.ToInt64() == owner || !IsWindowVisible(window)) return true;
            uint process; GetWindowThreadProcessId(window, out process);
            if (process == ownerProcess) { result.Add(window); return true; }
            var parent = window;
            for (int i = 0; i < 16; i++) {
                parent = GetWindow(parent, 4);
                if (parent == IntPtr.Zero) break;
                if (parent.ToInt64() == owner) { result.Add(window); break; }
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    public static string ClassName(IntPtr window) { var name = new StringBuilder(256); GetClassName(window, name, name.Capacity); return name.ToString(); }
    public static string Controls(IntPtr parent) {
        var result = new List<string>();
        EnumChildWindows(parent, (child, _) => {
            var kind = ClassName(child);
            if (kind.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 || kind.IndexOf("Combo", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(kind + ":" + GetDlgCtrlID(child) + ":" + IsWindowVisible(child));
            return true;
        }, IntPtr.Zero);
        return String.Join(";", result);
    }
    public static bool SelectFile(IntPtr parent, string path) {
        IntPtr edit = IntPtr.Zero;
        EnumChildWindows(parent, (child, _) => {
            if (ClassName(child) != "ComboBoxEx32" || !IsWindowVisible(child) || GetDlgCtrlID(child) != 1148) return true;
            EnumChildWindows(child, (nested, data) => { if (ClassName(nested) == "Edit") edit = nested; return true; }, IntPtr.Zero);
            return edit == IntPtr.Zero;
        }, IntPtr.Zero);
        if (edit == IntPtr.Zero) EnumChildWindows(parent, (child, _) => {
            if (ClassName(child) == "Edit" && (GetDlgCtrlID(child) == 1152 || GetDlgCtrlID(child) == 1001) && IsWindowVisible(child)) { edit = child; return false; }
            return true;
        }, IntPtr.Zero);
        if (edit == IntPtr.Zero) return false;
        SendMessage(edit, 12, IntPtr.Zero, path);
        var actual = new StringBuilder(path.Length + 16);
        SendMessage(edit, 13, new IntPtr(actual.Capacity), actual);
        if (actual.ToString() != path) throw new InvalidOperationException("Native filename differs from fixture path");
        var button = GetDlgItem(parent, 1);
        if (button == IntPtr.Zero) throw new InvalidOperationException("Native picker commit button is missing");
        SendMessage(button, 245, IntPtr.Zero, (string)null);
        return true;
    }
}
'@
$taskDeadline = [DateTime]::UtcNow.AddSeconds(20)
$taskDialog = [IntPtr]::Zero
$taskSeen = New-Object 'System.Collections.Generic.HashSet[string]'
try {
    while ([DateTime]::UtcNow -lt $taskDeadline) {
        foreach ($taskWindow in [PickerWindows]::Owned($OwnerWindow)) {
            [void]$taskSeen.Add('window:' + [PickerWindows]::ClassName($taskWindow))
            [void]$taskSeen.Add('native:' + [PickerWindows]::Controls($taskWindow))
            if ([PickerWindows]::SelectFile($taskWindow, $taskValue)) { Write-Output 'PASS owned native filename control selected the fixture path'; exit 0 }
            try {
                $taskElement = [System.Windows.Automation.AutomationElement]::FromHandle($taskWindow)
                $taskEditCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Edit)
                $taskComboCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::ComboBox)
                $taskCondition = New-Object System.Windows.Automation.OrCondition $taskEditCondition, $taskComboCondition
                $taskEdits = $taskElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $taskCondition)
            }
            catch { continue } # Dialog/tooltip fragments can disappear while the native picker is building.
            foreach ($taskEdit in $taskEdits) {
                try { $taskId = $taskEdit.Current.AutomationId }
                catch { continue }
                [void]$taskSeen.Add('edit:' + $taskId)
                if ($taskId -notin @('1001', '1148', '1152', 'FileNameControlHost') -and $taskId -notmatch '(?i)filename|foldername') { continue }
                $taskPattern = $null
                if (!$taskEdit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$taskPattern)) { continue }
                $taskDialog = $taskWindow
                $taskPattern.SetValue($taskValue)
                if ($taskPattern.Current.Value -ne $taskValue) { throw 'Picker did not retain the fixture path' }
                $taskCommit = $null
                foreach ($taskButtonId in @('1', 'PrimaryButton', 'CommitButton')) {
                    $taskButtonCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $taskButtonId
                    $taskCommit = $taskElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $taskButtonCondition)
                    if ($null -ne $taskCommit) { break }
                }
                if ($null -eq $taskCommit) { throw 'Native picker commit button not found' }
                $taskCommit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Write-Output 'PASS owned native picker selected the fixture path'
                exit 0
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw ('No owned native picker filename field appeared; observed identifiers: ' + (($taskSeen | Select-Object -First 24) -join ', '))
}
catch {
    foreach ($taskWindow in [PickerWindows]::Owned($OwnerWindow)) { [void][PickerWindows]::PostMessage($taskWindow, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
    Write-Error $_
    exit 1
}
