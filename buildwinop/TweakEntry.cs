using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CornSystems;   // shared Dpi helper — byte-identical across Corn Systems repos

namespace Win11Optimizer
{
    public class TweakEntry
    {
        public string Name        { get; set; }
        public string Description { get; set; }
        public string Category    { get; set; }
        public string Icon        { get; set; }
        public bool   IsAdvanced  { get; set; }
        public bool   DefaultOn   { get; set; } = true;
        public string AdvancedKey { get; set; }
        public string TweakKey    { get; set; }
        public string WhatItChanges { get; set; }         // What this tweak actually changes — shown in hover tooltip
    }

}
