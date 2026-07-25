using System;
using System.Net;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace QQReborn.App.Services
{
    public static class ToastHelper
    {
        public static void ShowMessageToast(string senderName, string conversationId, string preview, string avatarUrl, bool isMuted)
        {
            if (isMuted || string.IsNullOrEmpty(conversationId) || conversationId == App.ActiveConversationId)
            {
                return;
            }

            // Final defense at the actual Windows Toast emission point: global mute-all,
            // per-conversation mute, and special-care break-through live here so every
            // call site cannot forget a gate.
            if (NotificationMuteGate.ShouldSuppressNotification(conversationId)) return;

            senderName = WebUtility.HtmlEncode(senderName ?? "Unknown");
            preview = WebUtility.HtmlEncode(preview ?? "");
            avatarUrl = (avatarUrl ?? "").Trim();

            // Only attach an avatar node when we have a real URL/path. An empty src makes
            // some WP builds fall back to a broken/default tile and looks like "wrong avatar".
            string imageNode = "";
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                imageNode = Environment.NewLine + "      <image placement='appLogoOverride' hint-crop='circle' src='"
                    + WebUtility.HtmlEncode(avatarUrl) + "'/>";
            }

            string xmlStr =
                "<toast>" + Environment.NewLine
                + "  <visual>" + Environment.NewLine
                + "    <binding template='ToastGeneric'>" + Environment.NewLine
                + "      <text>" + senderName + "</text>" + Environment.NewLine
                + "      <text>" + preview + "</text>" + imageNode + Environment.NewLine
                + "    </binding>" + Environment.NewLine
                + "  </visual>" + Environment.NewLine
                + "</toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xmlStr);
            var toast = new ToastNotification(doc)
            {
                Tag = conversationId
            };

            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
    }
}
