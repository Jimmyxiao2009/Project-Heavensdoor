using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using QQReborn.App.Models;
using QQReborn.App.Mvvm;
using QQReborn.App.Services;

namespace QQReborn.App.ViewModels
{
    /// <summary>
    /// Drives the global search page. As <see cref="Query"/> changes it re-runs the
    /// search and republishes grouped <see cref="Sections"/>. Stale runs are dropped
    /// so fast typing never shows out-of-order results.
    /// </summary>
    public class SearchViewModel : ObservableObject
    {
        private readonly ISearchService _search;
        private int _runToken;

        public SearchViewModel(ISearchService search)
        {
            _search = search;
        }

        /// <summary>Grouped result sections: 联系人 / 群聊 / 聊天记录.</summary>
        public ObservableCollection<SearchSection> Sections { get; } = new ObservableCollection<SearchSection>();

        private string _query;
        public string Query
        {
            get => _query;
            set
            {
                if (Set(ref _query, value))
                {
                    RaisePropertyChanged(nameof(HasQuery));
                    // Fire-and-forget; RunSearchAsync guards against stale results.
                    _ = RunSearchAsync(value);
                }
            }
        }

        /// <summary>True once the user has typed a non-empty query.</summary>
        public bool HasQuery => !string.IsNullOrWhiteSpace(_query);

        private bool _hasResults;
        public bool HasResults { get => _hasResults; private set => Set(ref _hasResults, value); }

        /// <summary>Show the "无匹配结果" empty state: query present but nothing matched.</summary>
        private bool _showEmptyState;
        public bool ShowEmptyState { get => _showEmptyState; private set => Set(ref _showEmptyState, value); }

        /// <summary>Show the idle hint (query empty).</summary>
        private bool _showHint = true;
        public bool ShowHint { get => _showHint; private set => Set(ref _showHint, value); }

        private async Task RunSearchAsync(string query)
        {
            int token = Interlocked.Increment(ref _runToken);

            if (string.IsNullOrWhiteSpace(query))
            {
                Sections.Clear();
                HasResults = false;
                ShowEmptyState = false;
                ShowHint = true;
                return;
            }

            var results = await _search.SearchAsync(query);

            // Drop if a newer keystroke superseded this run.
            if (token != _runToken) return;

            Sections.Clear();
            foreach (var s in results) Sections.Add(s);

            HasResults = Sections.Count > 0;
            ShowEmptyState = !HasResults;
            ShowHint = false;
        }
    }
}
