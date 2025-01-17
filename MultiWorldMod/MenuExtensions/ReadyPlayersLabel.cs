﻿using MenuChanger.MenuElements;
using UnityEngine.UI;

namespace MultiWorldMod.MenuExtensions
{
    internal class ReadyPlayersLabel : MenuLabel
    {
        public ReadyPlayersLabel(MenuChanger.MenuPage menuPage, string text, Style style) 
            : base(menuPage, text, style)
        {
            Text.alignment = UnityEngine.TextAnchor.MiddleCenter;
        }

        public void SetText(string text)
        {
            Text.text = Align(text);
        }

        private string Align(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string[] names = text.Split(new string[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
            if (names.Length == 0)
            {
                return "The hell did you put for your name?";
            }

            string aligned = names[0];
            int accumulatedLength = aligned.Length;
            for (int i = 1; i < names.Length; i++)
            {
                if (accumulatedLength + names.Length + 2 > 32)
                {
                    aligned += ",\n" + names[i];
                    accumulatedLength = names[i].Length;
                }
                else
                {
                    aligned += ", " + names[i];
                    accumulatedLength += 2 + names[i].Length;
                }
            }

            return aligned;
        }
    }
}
