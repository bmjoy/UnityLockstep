﻿using Entitas;
using Lockstep.Core.Data;

namespace Lockstep.Core.Components.Input
{
    [Input]
    public class PlayerIdComponent : IComponent
    {
        public PlayerId value;
    }
}