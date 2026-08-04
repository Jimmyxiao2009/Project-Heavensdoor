using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using QQReborn.App.Mvvm;

namespace QQReborn.App.Models
{
    public enum ConversationKind
    {
        Friend,
        Group
    }
    public enum MessageDirection
    {
        Incoming,
        Outgoing
    }
    public enum MessageContentType
    {
        Text,
        Image,
        Sticker,
        Voice,
        System,
        LinkCard,
        FileMsg,
        Location,
        Video,
        Forward
    }
    public enum MessageState
    {
        Sending,
        Sent,
        Failed
    }
    public enum OnlineStatus
    {
        Online,
        Away,
        Busy,
        DoNotDisturb,
        Invisible
    }

    /// <summary>Identity of the logged-in user.</summary>
}
