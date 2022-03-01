using System.Collections.Generic;

namespace GenerationalApp
{
    internal sealed class TrieNode<TValue>
    {
        public TrieNode(char keyElement)
        {
            KeyElement = keyElement;
            Children = new Dictionary<char, TrieNode<TValue>>(31);
        }


        public char KeyElement { get; }

        public string? Key { get; set; }

        public TValue Value { get; set; }

        public Dictionary<char, TrieNode<TValue>> Children { get; }

        public TrieNode<TValue> Parent { get; set; }


        public int CountChildren()
        {
            int count = 0;
            foreach (var child in Children.Values)
            {
                if (child.Key is not null)
                {
                    count++;
                }

                count += child.CountChildren();
            }

            return count;
        }
    }
}