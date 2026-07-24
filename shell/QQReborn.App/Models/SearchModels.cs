using System;
using System.Collections.Generic;

namespace QQReborn.App.Models
{
    /// <summary>What kind of thing a search hit points at.</summary>
    public enum SearchResultKind
    {
        Contact,
        Group,
        Message
    }

    /// <summary>
    /// A single row in the global-search results list. Carries enough to render a
    /// Metro row (square avatar + title + secondary line) and to navigate to the
    /// right <see cref="ChatConversation"/> when tapped.
    /// </summary>
    public class SearchResult
    {
        public SearchResultKind Kind { get; set; }

        /// <summary>Primary line: contact name / group title / conversation title.</summary>
        public string Title { get; set; }

        /// <summary>Secondary line: signature, group preview, or matched message snippet.</summary>
        public string Subtitle { get; set; }

        public string AvatarPath { get; set; }

        /// <summary>For Message hits, the time of the matched message (else empty).</summary>
        public string TimeText { get; set; }

        /// <summary>Whether to show the time column (Message hits only).</summary>
        public bool HasTime { get; set; }

        /// <summary>The conversation to open when this row is tapped.</summary>
        public ChatConversation Target { get; set; }
    }

    /// <summary>A titled block of <see cref="SearchResult"/> rows (联系人 / 群聊 / 聊天记录).</summary>
    public class SearchSection
    {
        public string Header { get; set; }
        public IReadOnlyList<SearchResult> Items { get; set; }
    }
}
