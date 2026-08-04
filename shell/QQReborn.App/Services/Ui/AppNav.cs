using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using QQReborn.App.Views;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Root vs shell-content navigation. Detail pages (chat, settings) use the root frame
    /// so the hamburger / bottom bar chrome does not stay on top of them.
    /// </summary>
    public static class AppNav
    {
        public static Frame RootFrame => Window.Current?.Content as Frame;

        public static ShellPage CurrentShell
        {
            get
            {
                var root = RootFrame;
                return root?.Content as ShellPage;
            }
        }

        public static void ToRoot(Type pageType, object parameter = null)
        {
            var root = RootFrame;
            if (root == null) return;
            root.Navigate(pageType, parameter);
        }

        public static void ToShellHome(string section = null)
        {
            var root = RootFrame;
            if (root == null) return;
            if (root.Content is ShellPage shell)
            {
                shell.ShowSection(section ?? ShellPage.SectionChats);
                return;
            }
            root.Navigate(typeof(ShellPage), section ?? ShellPage.SectionChats);
        }

        public static bool GoBack()
        {
            var root = RootFrame;
            if (root != null && root.CanGoBack)
            {
                root.GoBack();
                return true;
            }
            return false;
        }
    }
}
