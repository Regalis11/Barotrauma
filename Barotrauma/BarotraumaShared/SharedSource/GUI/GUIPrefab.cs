namespace Barotrauma
{
    // Implemented in shared so clients can share fonts with eachother.
    // Required for font selection by `ItemLabel`s.

    public abstract partial class GUISelector<T>
    {
        public readonly Identifier Identifier;

        public GUISelector(string identifier) => Identifier = identifier.ToIdentifier();
    }

    public partial class GUIFontPrefab { }

    public partial class GUIFont : GUISelector<GUIFontPrefab>
    {
        public GUIFont(string identifier) : base(identifier) { }
    }
}
