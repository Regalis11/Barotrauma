﻿using System.Collections.Generic;
using Microsoft.Xna.Framework;
using OpenTK.Audio.OpenAL;
using OpenTK.Audio;

using System;

namespace Barotrauma.Sounds
{
    static class SoundManager
    {
        public const int DefaultSourceCount = 16;

        private static readonly List<int> alSources = new List<int>();
        private static readonly int[] alBuffers = new int[DefaultSourceCount];
        private static int lowpassFilterId;

        private static readonly Sound[] soundsPlaying = new Sound[DefaultSourceCount];
        
        private static AudioContext AC;

        private static OggStreamer oggStreamer;
        private static OggStream oggStream;

        public static float MasterVolume = 1.0f;

        public static void Init()
        {
            try
            {
                AC = new AudioContext();
            }
            catch (DllNotFoundException e)
            {
                Program.CrashMessageBox("OpenAL32.dll not found");
                throw e;
            }

            for (int i = 0 ; i < DefaultSourceCount; i++)
            {
                alSources.Add(OpenTK.Audio.OpenAL.AL.GenSource());
            }
            ALHelper.Check();
            if (ALHelper.Efx.IsInitialized)
            {
                lowpassFilterId = ALHelper.Efx.GenFilter();
                //alFilters.Add(alFilterId);
                ALHelper.Efx.Filter(lowpassFilterId, OpenTK.Audio.OpenAL.EfxFilteri.FilterType, (int)OpenTK.Audio.OpenAL.EfxFilterType.Lowpass);
                                
                LowPassHFGain = 1.0f;
            }
        }


        public static int Play(Sound sound, float volume = 1.0f)
        {
            return Play(sound, Vector2.Zero, volume, 0.0f);
        }

        public static int Play(Sound sound, Vector2 position, float volume = 1.0f, float lowPassGain = 0.0f, bool loop=false)
        {
            for (int i = 1; i < DefaultSourceCount; i++)
            {
                //find a source that's free to use (not playing or paused)
                if (OpenTK.Audio.OpenAL.AL.GetSourceState(alSources[i]) == OpenTK.Audio.OpenAL.ALSourceState.Playing
                    || OpenTK.Audio.OpenAL.AL.GetSourceState(alSources[i]) == OpenTK.Audio.OpenAL.ALSourceState.Paused) continue;

                soundsPlaying[i] = sound;

                alBuffers[i] = sound.AlBufferId;
                OpenTK.Audio.OpenAL.AL.Source(alSources[i], OpenTK.Audio.OpenAL.ALSourceb.Looping, loop);

                OpenTK.Audio.OpenAL.AL.Source(alSources[i], OpenTK.Audio.OpenAL.ALSourcei.Buffer, sound.AlBufferId);
                
                UpdateSoundPosition(i, position, volume);

                OpenTK.Audio.OpenAL.AL.SourcePlay(alSources[i]);

                return i;
            }

            return -1;
        }

        public static int Loop(Sound sound, int sourceIndex, float volume = 1.0f)
        {
            return Loop(sound,sourceIndex, Vector2.Zero, volume);
        }

        public static int Loop(Sound sound, int sourceIndex, Vector2 position, float volume = 1.0f)
        {
            if (!MathUtils.IsValid(volume))
            {
                volume = 0.0f;
            }

            if (sourceIndex<1)
            {
                sourceIndex = Play(sound, position, volume, 0.0f, true);
            }
            else
            {
                UpdateSoundPosition(sourceIndex, position, volume);
                AL.Source(alSources[sourceIndex], ALSourceb.Looping, true);
            }

            ALHelper.Check();
            return sourceIndex;
        }

        public static void Pause(int sourceIndex)
        {
            if (AL.GetSourceState(alSources[sourceIndex]) != ALSourceState.Playing)
                return;

            AL.SourcePause(alSources[sourceIndex]);
            ALHelper.Check();
        }

        public static void Resume(int sourceIndex)
        {
            if (AL.GetSourceState(alSources[sourceIndex]) != ALSourceState.Paused)
                return;

            AL.SourcePlay(alSources[sourceIndex]);
            ALHelper.Check();
        }
        
        public static void Stop(int sourceIndex)
        {
            if (sourceIndex < 1) return;
            
            var state = AL.GetSourceState(alSources[sourceIndex]);
            if (state == ALSourceState.Playing || state == ALSourceState.Paused)
            {
                AL.SourceStop(alSources[sourceIndex]);
                AL.Source(alSources[sourceIndex], ALSourceb.Looping, false);

                soundsPlaying[sourceIndex] = null;
            }
        }

        public static Sound GetPlayingSound(int sourceIndex)
        {
            if (sourceIndex < 1 || sourceIndex>alSources.Count-1) return null;

            if (AL.GetSourceState(alSources[sourceIndex]) != ALSourceState.Playing) return null;

            return soundsPlaying[sourceIndex];
        }

        public static bool IsPlaying(int sourceIndex)
        {
            if (sourceIndex < 1 || sourceIndex>alSources.Count-1) return false;

            return AL.GetSourceState(alSources[sourceIndex]) == ALSourceState.Playing;
        }

        public static bool IsPaused(int sourceIndex)
        {
            if (sourceIndex < 1 || sourceIndex > alSources.Count - 1) return false;
            
            return AL.GetSourceState(alSources[sourceIndex]) == ALSourceState.Paused;
        }

        public static bool IsLooping(int sourceIndex)
        {
            if (sourceIndex < 1 || sourceIndex > alSources.Count - 1) return false;

            bool isLooping;            
            
            OpenTK.Audio.OpenAL.AL.GetSource(alSources[sourceIndex], OpenTK.Audio.OpenAL.ALSourceb.Looping, out isLooping);

            return isLooping;
        }

        public static void Volume(int sourceIndex, float volume)
        {
            AL.Source(alSources[sourceIndex], ALSourcef.Gain, volume * MasterVolume);
            ALHelper.Check();
        }

        static float lowPassHfGain;
        public static float LowPassHFGain
        {
            get { return lowPassHfGain; }
            set
            {
                if (ALHelper.Efx.IsInitialized)
                {
                    lowPassHfGain = value;
                    for (int i = 0; i < DefaultSourceCount; i++)
                    {
                        //find a source that's free to use (not playing or paused)
                        if (OpenTK.Audio.OpenAL.AL.GetSourceState(alSources[i]) != OpenTK.Audio.OpenAL.ALSourceState.Playing
                            && OpenTK.Audio.OpenAL.AL.GetSourceState(alSources[i])!= OpenTK.Audio.OpenAL.ALSourceState.Paused) continue;

                        ALHelper.Efx.Filter(lowpassFilterId, OpenTK.Audio.OpenAL.EfxFilterf.LowpassGainHF, lowPassHfGain = value);                        
                        ALHelper.Efx.BindFilterToSource(alSources[i], lowpassFilterId);
                        ALHelper.Check();
                    }

                }
            }
        }


        public static void UpdateSoundPosition(int sourceIndex, Vector2 position, float baseVolume = 1.0f)
        {
            if (sourceIndex < 1) return;

            if (!MathUtils.IsValid(position))
            {
                position = Vector2.Zero;
            }

            position /= 1000.0f;

            OpenTK.Audio.OpenAL.AL.Source(alSources[sourceIndex], OpenTK.Audio.OpenAL.ALSourcef.Gain, baseVolume * MasterVolume);
            OpenTK.Audio.OpenAL.AL.Source(alSources[sourceIndex], OpenTK.Audio.OpenAL.ALSource3f.Position, position.X, position.Y, 0.0f);

            float lowPassGain = lowPassHfGain / Math.Max(position.Length() * 5.0f, 1.0f);

            ALHelper.Efx.Filter(lowpassFilterId, OpenTK.Audio.OpenAL.EfxFilterf.LowpassGainHF, lowPassGain);
            ALHelper.Efx.BindFilterToSource(alSources[sourceIndex], lowpassFilterId);
            ALHelper.Check();
        }

        public static OggStream StartStream(string file, float volume = 1.0f)
        {
            if (oggStreamer == null)
                oggStreamer = new OggStreamer();

            oggStream = new OggStream(file);            
            oggStreamer.AddStream(oggStream);           

            oggStream.Play(volume);

            ALHelper.Check();

            return oggStream;
        }

        public static void StopStream()
        {
            if (oggStream!=null) oggStream.Stop();
        }

        public static void ClearAlSource(int bufferId)
        {
            for (int i = 1; i < DefaultSourceCount; i++)
            {
                if (alBuffers[i] != bufferId) continue;
                
                OpenTK.Audio.OpenAL.AL.Source(alSources[i], OpenTK.Audio.OpenAL.ALSourceb.Looping, false);
                OpenTK.Audio.OpenAL.AL.Source(alSources[i], OpenTK.Audio.OpenAL.ALSourcei.Buffer, 0);                
            }             
        }
        
        public static void Dispose()
        {
            if (ALHelper.Efx.IsInitialized)
                ALHelper.Efx.DeleteFilter(lowpassFilterId);

            for (int i = 0; i < DefaultSourceCount; i++)
            {
                var state = OpenTK.Audio.OpenAL.AL.GetSourceState(alSources[i]);
                if (state == OpenTK.Audio.OpenAL.ALSourceState.Playing || state == OpenTK.Audio.OpenAL.ALSourceState.Paused)
                    Stop(i);

                OpenTK.Audio.OpenAL.AL.DeleteSource(alSources[i]);
                
                ALHelper.Check();
            }

            if (oggStream!=null)
            {
                oggStream.Stop();
                oggStream.Dispose();
            }
            
            if (oggStreamer != null)
                oggStreamer.Dispose();
        }

    }
}
