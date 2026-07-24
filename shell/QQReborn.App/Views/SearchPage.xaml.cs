using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using QQReborn.App.Models;
using QQReborn.App.Services;
using QQReborn.App.ViewModels;

namespace QQReborn.App.Views
{
    public sealed partial class SearchPage : Page
    {
        private readonly SearchViewModel _vm;

        public SearchPage()
        {
            InitializeComponent();
            // Self-contained search backend driven by the live chat singleton (read-only).
            _vm = new SearchViewModel(new MockSearchService(App.ChatService));
            DataContext = _vm;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // Focus the box so the soft keyboard / cursor lands here immediately.
            SearchBox.Focus(FocusState.Programmatic);
        }

        private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult result && result.Target != null)
            {
                Frame.Navigate(typeof(ConversationPage), result.Target);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
