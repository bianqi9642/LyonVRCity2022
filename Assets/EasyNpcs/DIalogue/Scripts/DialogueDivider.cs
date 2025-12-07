using System.Collections.Generic;
using UnityEngine;

namespace Dialogue_Package
{
    public static class DialogueDivider
    {
        /// <summary>
        /// Splits a Sentence's npcText into 200-char chunks.
        /// Returns the head Sentence (original givenSentence is reused as the first chunk).
        /// If givenSentence is null or text length <= 200, returns the original sentence (or null if input was null).
        /// </summary>
        public static Sentence DivideDialogue(Sentence givenSentence)
        {
            if (givenSentence == null) return null;

            string text = givenSentence.npcText;
            if (string.IsNullOrEmpty(text)) return givenSentence;

            const int chunkSize = 200;
            int length = text.Length;

            // Nothing to split
            if (length <= chunkSize)
            {
                return givenSentence;
            }

            // Prepare first chunk in the original Sentence (so external references keep pointing to the head)
            givenSentence.npcText = text.Substring(0, chunkSize);
            Sentence previous = givenSentence;

            // Create remaining chunks
            for (int start = chunkSize; start < length; start += chunkSize)
            {
                int remaining = length - start;
                int currentSize = remaining < chunkSize ? remaining : chunkSize;

                Sentence newSentence = new Sentence();
                newSentence.npcText = text.Substring(start, currentSize);

                previous.nextSentence = newSentence;
                previous = newSentence;
            }

            // head is the original givenSentence (now containing the first chunk)
            return givenSentence;
        }
    }
}
