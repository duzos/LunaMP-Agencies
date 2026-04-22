using LmpClient.Localization;
using LmpClient.Systems.Chat;
using LmpClient.Systems.PlayerColorSys;
using LmpCommon.Message.Types;
using UnityEngine;

namespace LmpClient.Windows.Chat
{
    public partial class ChatWindow
    {
        private static string _chatInputText = string.Empty;
        private static ChatChannel _channel = ChatChannel.Global;

        protected override void DrawWindowContent(int windowId)
        {
            var pressedEnter = Event.current.type == EventType.KeyDown && !Event.current.shift && Event.current.character == '\n';
            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            DrawChannelBar();
            DrawChatMessageBox();
            DrawTextInput(pressedEnter);
            GUILayout.Space(5);
            GUILayout.EndVertical();
        }

        private static readonly string[] _channelLabels = { "Global", "Agency" };

        private static void DrawChannelBar()
        {
            // Use a Toolbar so the two options render as a clean segmented
            // control instead of two GUILayout.Toggle widgets that overlap
            // when the chat window is narrow.
            var idx = _channel == ChatChannel.Agency ? 1 : 0;
            idx = GUILayout.Toolbar(idx, _channelLabels);
            _channel = idx == 1 ? ChatChannel.Agency : ChatChannel.Global;
        }

        private static void DrawChatMessageBox()
        {
            _chatScrollPos = GUILayout.BeginScrollView(_chatScrollPos);
            GUILayout.BeginVertical();
            GUILayout.FlexibleSpace();

            foreach (var chatMsg in ChatSystem.Singleton.ChatMessages)
            {
                _playerNameStyle.normal.textColor = PlayerColorSystem.Singleton.GetPlayerColor(chatMsg.Item1);
                GUILayout.Label(chatMsg.Item3, _playerNameStyle);
            }

            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }

        private static void DrawTextInput(bool pressedEnter)
        {
            GUILayout.BeginHorizontal();

            if (pressedEnter || GUILayout.Button(LocalizationContainer.ChatWindowText.Send, GUILayout.Width(WindowWidth * .25f)))
            {
                if (!string.IsNullOrEmpty(_chatInputText))
                {
                    ChatSystem.Singleton.MessageSender.SendChatMsg(_chatInputText.Trim('\n'), _channel);
                }

                _chatInputText = string.Empty;
            }
            else
            {
                _chatInputText = GUILayout.TextArea(_chatInputText);
            }

            GUILayout.EndHorizontal();
        }
    }
}