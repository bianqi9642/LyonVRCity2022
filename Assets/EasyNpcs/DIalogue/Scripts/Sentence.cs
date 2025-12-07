using System.Collections.Generic;
using UnityEngine;

namespace Dialogue_Package
{
    [CreateAssetMenu(fileName = "Sentence.asset", menuName = "Dialogue/Sentence")]
    public class Sentence : ScriptableObject
    {
        public string playerText;
        public string npcText;

        public Sentence nextSentence;
        public List<Sentence> choices = new List<Sentence>();
    }
}
