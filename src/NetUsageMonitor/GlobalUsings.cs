// This app uses both WPF and WinForms (WinForms only for the tray NotifyIcon and icon interop).
// WinForms contributes global usings (System.Windows.Forms, System.Drawing) that collide with WPF
// type names. These aliases make the ambiguous names resolve to their WPF versions everywhere.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Clipboard = System.Windows.Clipboard;
global using Binding = System.Windows.Data.Binding;
global using Color = System.Windows.Media.Color;
global using Brush = System.Windows.Media.Brush;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
