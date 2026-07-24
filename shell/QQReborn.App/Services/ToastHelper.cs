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
            // per-conversation mute, and 特别关心 break-through live here so every
            // call site cannot forget a gate.
            if (NotificationMuteGate.ShouldSuppressNotification(conversationId)) return;

            senderName = WebUtility.HtmlEncode(senderName ?? "Unknown");
            preview = WebUtility.HtmlEncode(preview ?? "");
            avatarUrl = WebUtility.HtmlEncode(avatarUrl ?? "");

            string xmlStr = $@"<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>{senderName}</text>
      <text>{preview}</text>
      <image placement='appLogoOverride' hint-crop='circle' src='{avatarUrl}'/>
    </binding>
  </visual>
</toast>";

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
