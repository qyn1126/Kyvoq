using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kyvoq.Core.Models;

namespace Kyvoq.App.ViewModels;

/// <summary>
/// 为本机 Steam 账号提供昵称、状态和冻结的本地头像。
/// </summary>
public sealed class SteamAccountViewModel : ObservableObject
{
    private string note = string.Empty;

    public SteamAccount Account { get; }
    public string Note => note;
    public string DisplayName => string.IsNullOrWhiteSpace(note) ? Account.DisplayName : note;
    public string AccountName => Account.AccountName;
    public string Initial => System.Globalization.StringInfo.GetNextTextElement(DisplayName);
    public string Status => Account.IsCurrent ? "当前账号" : Account.IsLastUsed ? "上次使用" : string.Empty;
    public string StatusBadge => Account.IsCurrent ? "当前" : Account.IsLastUsed ? "上次" : string.Empty;
    public string ToolTip => (note.Length > 0 ? $"备注：{note}\n" : string.Empty)
        + $"昵称：{Account.DisplayName}\n登录名：{AccountName}\nSteamID: {Account.SteamId64}"
        + (Status.Length > 0 ? $"\n{Status}" : string.Empty);
    public ImageSource? Avatar { get; }

    /// <summary>
    /// 保存账号资料和已加载头像。
    /// </summary>
    private SteamAccountViewModel(SteamAccount account, ImageSource? avatar)
    {
        Account = account;
        Avatar = avatar;
    }

    /// <summary>只在备注成功保存或读取后更新界面文字。</summary>
    public void SetNote(string value)
    {
        if (SetProperty(ref note, value.Trim(), nameof(Note)))
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Initial));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>
    /// 后台读取头像，缺失或损坏时使用文字占位且不锁定缓存文件。
    /// </summary>
    public static Task<SteamAccountViewModel> CreateAsync(SteamAccount account, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            ImageSource? avatar = null;
            if (account.AvatarPath is { } path)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 96;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    avatar = bitmap;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException
                    or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // Steam 正在替换头像或缓存损坏时继续显示账号。
                }
            }

            return new SteamAccountViewModel(account, avatar);
        }, cancellationToken);
}
