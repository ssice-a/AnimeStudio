using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnimeStudio
{
    public class NamedObject : EditorExtension
    {
        public string m_Name;

        public override string Name => m_Name;

        protected NamedObject(Object source, string name) : base(source) { m_Name = name; }

        protected NamedObject(ObjectReader reader) : base(reader)
        {
            m_Name = reader.ReadAlignedString();
        }
    }
}
