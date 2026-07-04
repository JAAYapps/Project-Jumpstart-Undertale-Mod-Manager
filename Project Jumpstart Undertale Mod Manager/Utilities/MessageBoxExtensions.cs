using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace Project_Jumpstart_Undertale_Mod_Manager.Utilities;

/// <summary>
/// Provides <see cref="MessageBoxManager"/> extensions for <see cref="Application"/>s.
/// </summary>
public static class MessageBoxExtensions
{
    /// <summary>
		/// Shows an informational <see cref="MessageBoxManager"/> with <paramref name="app"/> as the parent.
		/// </summary>
		/// <param name="app">The parent from which the <see cref="MessageBoxManager"/> will show.</param>
		/// <param name="messageBoxText">A <see cref="string"/> that specifies the text to display.</param>
		/// <param name="title">A <see cref="string"/> that specifies the title bar caption to display.</param>
		/// <returns><see cref="ButtonEnum.Ok"/> or <see cref="ButtonResult.None"/> if
		/// the <see cref="MessageBoxManager"/> was cancelled.</returns>
		public static async Task<ButtonResult> ShowMessage(this Application app, string messageBoxText, string title = "UndertaleModTool")
		{
			return await app.ShowCore(messageBoxText, title, ButtonEnum.Ok, Icon.Info);
		}

		/// <summary>
		/// Shows a <see cref="MessageBoxManager"/> prompting for a yes/no question with <paramref name="app"/> as the parent.
		/// </summary>
		/// <param name="app">The parent from which the <see cref="MessageBoxManager"/> will show.</param>
		/// <param name="messageBoxText">A <see cref="string"/> that specifies the text to display.</param>
		/// <param name="icon">The <see cref="Icon"/> to display.</param>
		/// <param name="title">A <see cref="string"/> that specifies the title bar caption to display.</param>
		/// <returns><see cref="ButtonResult.Yes"/> or <see cref="ButtonResult.No"/> depending on the users' answer.
		/// <see cref="ButtonResult.None"/> if the <see cref="MessageBoxManager"/> was cancelled.</returns>
		public static async Task<ButtonResult> ShowQuestion(this Application app, string messageBoxText, Icon icon = Icon.Question, string title = "UndertaleModTool")
		{
			return await app.ShowCore(messageBoxText, title, ButtonEnum.YesNo, icon);
		}

		/// <summary>
		/// Shows a <see cref="MessageBoxManager"/> prompting for a yes/no/cancel question with <paramref name="app"/> as the parent.
		/// </summary>
		/// <param name="app">The parent from which the <see cref="MessageBoxManager"/> will show.</param>
		/// <param name="messageBoxText">A <see cref="string"/> that specifies the text to display.</param>
		/// <param name="icon">The <see cref="Icon"/> to display.</param>
		/// <param name="title">A <see cref="string"/> that specifies the title bar caption to display.</param>
		/// <returns/><see cref="ButtonResult.Yes"/>, <see cref="ButtonResult.No"/> or <see cref="ButtonResult.Cancel"/> depending on the users' answer.
		public static async Task<ButtonResult> ShowQuestionWithCancel(this Application app, string messageBoxText, Icon icon = Icon.Question, string title = "UndertaleModTool")
		{
			return await app.ShowCore(messageBoxText, title, ButtonEnum.YesNoCancel, icon);
		}

		/// <summary>
		/// Shows a warning <see cref="MessageBoxManager"/> with <paramref name="app"/> as the parent.
		/// </summary>
		/// <param name="app">The parent from which the <see cref="MessageBoxManager"/> will show.</param>
		/// <param name="messageBoxText">A <see cref="string"/> that specifies the text to display.</param>
		/// <param name="title">A <see cref="string"/> that specifies the title bar caption to display.</param>
		/// <returns><see cref="ButtonResult.Ok"/> or <see cref="ButtonResult.None"/> if
		/// the <see cref="MessageBoxManager"/> was cancelled.</returns>
		public static async Task<ButtonResult> ShowWarning(this Application app, string messageBoxText, string title = "Warning")
		{
			return await app.ShowCore(messageBoxText, title, ButtonEnum.Ok, Icon.Warning);
		}

		/// <summary>
		/// Shows an error <see cref="MessageBoxManager"/> with <paramref name="app"/> as the parent.
		/// </summary>
		/// <param name="app">The parent from which the <see cref="MessageBoxManager"/> will show.</param>
		/// <param name="messageBoxText">A <see cref="string"/> that specifies the text to display.</param>
		/// <param name="title">A <see cref="string"/> that specifies the title bar caption to display.</param>
		/// <returns><see cref="ButtonResult.Ok"/> or <see cref="ButtonResult.None"/> if
		/// the <see cref="MessageBoxManager"/> was cancelled.</returns>
		public static async Task<ButtonResult> ShowError(this Application app, string messageBoxText, string title = "Error")
		{
			return await app.ShowCore(messageBoxText, title, ButtonEnum.Ok, Icon.Error);
		}

		/// <summary>
		/// The wrapper for the extensions to directly call <see cref="MessageBoxManager.GetMessageBoxStandard(string, string, ButtonEnum, Icon, WindowStartupLocation)"/> and it's ShowAsync method.
		/// </summary>
		/// <param name="app">The app that represents the owner of the message box.</param>
		/// <param name="text">A <see cref="string"/> that specifies the text to display.</param>
		/// <param name="title">A <see cref="string"/> that specifies the title bar caption to display.</param>
		/// <param name="buttons">A <see cref="ButtonEnum"/> value that specifies which button or buttons to display.</param>
		/// <param name="image">A <see cref="Icon"/> value that specifies the icon to display.</param>
		/// <returns>A task of <see cref="ButtonResult"/> value that specifies which message box button is clicked by the user.</returns>
		private static async Task<ButtonResult> ShowCore(this Application app, string text, string title, ButtonEnum buttons, Icon image)
		{
			return await MessageBoxManager.GetMessageBoxStandard(title, text, buttons, image).ShowAsync();
		}
}