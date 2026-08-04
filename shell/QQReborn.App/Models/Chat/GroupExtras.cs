using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using QQReborn.App.Mvvm;

namespace QQReborn.App.Models
{
    public class GroupNoticeItem
    {
        public string Id { get; set; }
        public string Content { get; set; }
        public long Time { get; set; }
    }

    /// <summary>File or folder entry from NapCat group file APIs.</summary>
    public class GroupFileEntry
    {
        public bool IsFolder { get; set; }
        public string FileId { get; set; }
        public string FolderId { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public int Busid { get; set; }
        public string Uploader { get; set; }

        public string SizeText
        {
            get
            {
                if (IsFolder) return "文件夹";
                if (Size < 1024) return Size + " B";
                if (Size < 1024 * 1024) return (Size / 1024.0).ToString("0.#") + " KB";
                if (Size < 1024L * 1024 * 1024) return (Size / (1024.0 * 1024)).ToString("0.#") + " MB";
                return (Size / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
            }
        }
    }
    public class GroupFilesResult
    {
        public System.Collections.Generic.List<GroupFileEntry> Folders { get; } =
            new System.Collections.Generic.List<GroupFileEntry>();
        public System.Collections.Generic.List<GroupFileEntry> Files { get; } =
            new System.Collections.Generic.List<GroupFileEntry>();
    }

    /// <summary>A pending friend request.</summary>
}
