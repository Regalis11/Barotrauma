﻿// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;

namespace Microsoft.Xna.Framework.Graphics
{
    public sealed partial class TextureCollection
    {
        private readonly GraphicsDevice _graphicsDevice;
        private readonly Texture[] _textures;
        private readonly bool _applyToVertexStage;
        private int _dirty;
        private int _dirtyMax;

        internal TextureCollection(GraphicsDevice graphicsDevice, int maxTextures, bool applyToVertexStage)
        {
            _graphicsDevice = graphicsDevice;
            _textures = new Texture[maxTextures];
            _applyToVertexStage = applyToVertexStage;
            for (int i = 0; i < maxTextures; i++)
            {
                _dirtyMax |= 1 << i;
            }
            _dirty = _dirtyMax;
            PlatformInit();
        }

        public Texture this[int index]
        {
            get
            {
                return _textures[index];
            }
            set
            {
                if (_applyToVertexStage && !_graphicsDevice.GraphicsCapabilities.SupportsVertexTextures)
                    throw new NotSupportedException("Vertex textures are not supported on this device.");

                if (_textures[index] == value)
                    return;

                _textures[index] = value;
                _dirty |= 1 << index;
            }
        }

        internal void Clear()
        {
            for (var i = 0; i < _textures.Length; i++)
                _textures[i] = null;

            PlatformClear();
            _dirty = _dirtyMax;
        }

        /// <summary>
        /// Marks all texture slots as dirty.
        /// </summary>
        internal void Dirty()
        {
            _dirty = _dirtyMax;
        }

        internal void SetTextures(GraphicsDevice device)
        {
            if (_applyToVertexStage && !device.GraphicsCapabilities.SupportsVertexTextures)
                return;
            PlatformSetTextures(device);
        }
    }
}
