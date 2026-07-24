using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        /// <summary>Debounce delay before a keystroke actually triggers a search -- avoids
        /// firing a full search (which, against the remote backend, is several real WS
        /// round-trips) on every single keystroke.</summary>
        private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

        private readonly ISearchService _search;
        private int _runToken;
        private CancellationTokenSource _debounceCts;

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
                    // Fire-and-forget; DebounceAndSearchAsync guards against stale results
                    // and waits out DebounceDelay before actually searching.
                    _ = DebounceAndSearchAsync(value);
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

        /// <summary>Waits out <see cref="DebounceDelay"/>, cancelling any previous pending
        /// wait, then runs the actual search. Cancelling here (as opposed to only relying on
        /// the stale-token check in RunSearchAsync) means a fast typist never even issues the
        /// superseded network calls in the first place.</summary>
        private async Task DebounceAndSearchAsync(string query)
        {
            _debounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;

            try
            {
                await Task.Delay(DebounceDelay, cts.Token);
            }
            catch (TaskCanceledException)
            {
                return; // superseded by a newer keystroke
            }

            if (cts.IsCancellationRequested) return;

            await RunSearchAsync(query);
        }

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

            try
            {
                var results = await _search.SearchAsync(query);

                // Drop if a newer keystroke superseded this run.
                if (token != _runToken) return;

                Sections.Clear();
                foreach (var s in results) Sections.Add(s);

                HasResults = Sections.Count > 0;
                ShowEmptyState = !HasResults;
                ShowHint = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("SearchViewModel.RunSearchAsync failed: " + ex);

                // Drop if a newer keystroke superseded this run -- don't clobber its state.
                if (token != _runToken) return;

                // Don't leave the UI stuck spinning on a stale/failed run -- fall back to the
                // empty-state message rather than silently doing nothing.
                Sections.Clear();
                HasResults = false;
                ShowEmptyState = true;
                ShowHint = false;
                // Surface a one-shot section so remote timeouts aren't a blank "无匹配".
                Sections.Add(new SearchSection
                {
                    Header = "提示",
                    Items = new[]
                    {
                        new SearchResult
                        {
                            Kind = SearchResultKind.Message,
                            Title = "搜索失败",
                            Subtitle = string.IsNullOrEmpty(ex.Message) ? "请检查是否已登录并连上服务器" : ex.Message,
                            HasTime = false,
                        }
                    }
                });
                HasResults = true;
                ShowEmptyState = false;
            }
        }
    }
}
