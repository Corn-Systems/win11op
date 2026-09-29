namespace Win11Optimizer
{
    public class TweakEntry
    {
        public string Name          { get; set; }
        public string Description   { get; set; }
        public string Category      { get; set; }
        public string Icon          { get; set; }
        public bool   IsAdvanced    { get; set; }
        public bool   DefaultOn     { get; set; } = true;
        public string AdvancedKey   { get; set; }
        public string TweakKey      { get; set; }
        public string WhatItChanges { get; set; }   // what this tweak actually changes — shown in the hover tooltip
    }
}
