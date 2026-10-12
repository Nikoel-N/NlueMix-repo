// WPF 的 XAML 生成器会注入 System.Windows.Forms.Integration，进而把 WinForms 和 System.Drawing
// 也带进全局命名空间；这里做必要的消歧义，其余一律用 WPF 的类型。
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Button = System.Windows.Controls.Button;
global using TextBlock = System.Windows.Controls.TextBlock;

global using NAudio.Wave.SampleProviders;
