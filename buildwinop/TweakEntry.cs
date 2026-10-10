namespace Win11Optimizer
{
    public class tweakEntry
    {
        public string name          { get; set; }
        public string description   { get; set; }
        public string category      { get; set; }
        public string icon          { get; set; }
        public bool   isAdvanced    { get; set; }
        public bool   defaultOn     { get; set; } = true;
        public string advancedKey   { get; set; }
        public string tweakKey      { get; set; }
        public string whatItChanges { get; set; }   // what this tweak actually changes — shown in the hover tooltip
    }
}
