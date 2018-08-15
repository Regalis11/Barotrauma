﻿using Barotrauma.Networking;
using Barotrauma.Sounds;
using FarseerPhysics;
using Lidgren.Network;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class Submarine : Entity, IServerSerializable
    {
        public Sprite PreviewImage;

        private static List<Sound> roundSounds = null;

        public static Sound LoadRoundSound(string filename,bool stream=false)
        {
            if (roundSounds == null)
            {
                roundSounds = new List<Sound>();
            }
            else
            {
                Sound sound = roundSounds.Find(s => s.Filename == filename && s.Stream == stream);

                if (sound != null) return sound;
            }

            Sound newSound = GameMain.SoundManager.LoadSound(filename, stream);

            roundSounds.Add(newSound);
            return newSound;
        }

        public static Sound LoadRoundSound(XElement element, bool stream=false)
        {
            if (roundSounds == null)
            {
                roundSounds = new List<Sound>();
            }
            else
            {
                Sound sound = roundSounds.Find(s =>
                    s.Filename == element.GetAttributeString("file", "") &&
                    Math.Abs(s.BaseGain - element.GetAttributeFloat("volume", 1.0f)) < 0.01f &&
                    Math.Abs(s.BaseFar - element.GetAttributeFloat("range", 1000.0f)) < 0.01f &&
                    s.Stream == stream);

                if (sound != null) return sound;
            }
            Sound newSound = GameMain.SoundManager.LoadSound(element, stream);
            roundSounds.Add(newSound);
            return newSound;
        }

        public static void RemoveRoundSound(Sound sound)
        {
            sound.Dispose();
            if (roundSounds == null) return;
            if (roundSounds.Contains(sound)) roundSounds.Remove(sound);
        }

        public static void RemoveAllRoundSounds()
        {
            if (roundSounds == null) return;
            for (int i = roundSounds.Count - 1; i >= 0; i--)
            {
                RemoveRoundSound(roundSounds[i]);
            }
        }

        public static void Draw(SpriteBatch spriteBatch, bool editing = false)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                e.Draw(spriteBatch, editing);
            }
        }

        public static void DrawFront(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity> predicate = null)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                if (!e.DrawOverWater) continue;

                if (predicate != null)
                {
                    if (!predicate(e)) continue;
                }

                e.Draw(spriteBatch, editing, false);
            }

            if (GameMain.DebugDraw)
            {
                foreach (Submarine sub in Loaded)
                {
                    Rectangle worldBorders = sub.Borders;
                    worldBorders.Location += sub.WorldPosition.ToPoint();
                    worldBorders.Y = -worldBorders.Y;

                    GUI.DrawRectangle(spriteBatch, worldBorders, Color.White, false, 0, 5);

                    if (sub.subBody.MemPos.Count < 2) continue;

                    Vector2 prevPos = ConvertUnits.ToDisplayUnits(sub.subBody.MemPos[0].Position);
                    prevPos.Y = -prevPos.Y;

                    for (int i = 1; i < sub.subBody.MemPos.Count; i++)
                    {
                        Vector2 currPos = ConvertUnits.ToDisplayUnits(sub.subBody.MemPos[i].Position);
                        currPos.Y = -currPos.Y;

                        GUI.DrawRectangle(spriteBatch, new Rectangle((int)currPos.X - 10, (int)currPos.Y - 10, 20, 20), Color.Blue * 0.6f, true, 0.01f);
                        GUI.DrawLine(spriteBatch, prevPos, currPos, Color.Cyan * 0.5f, 0, 5);

                        prevPos = currPos;
                    }
                }
            }
        }

        public static float DamageEffectCutoff;
        public static Color DamageEffectColor;

        public static void DrawDamageable(SpriteBatch spriteBatch, Effect damageEffect, bool editing = false)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                if (e.DrawDamageEffect)
                    e.DrawDamage(spriteBatch, damageEffect);
            }
            if (damageEffect != null)
            {
                damageEffect.Parameters["aCutoff"].SetValue(0.0f);
                damageEffect.Parameters["cCutoff"].SetValue(0.0f);

                DamageEffectCutoff = 0.0f;
            }
        }

        public static void DrawBack(SpriteBatch spriteBatch, bool editing = false, Predicate<MapEntity> predicate = null)
        {
            var entitiesToRender = !editing && visibleEntities != null ? visibleEntities : MapEntity.mapEntityList;

            foreach (MapEntity e in entitiesToRender)
            {
                if (!e.DrawBelowWater) continue;

                if (predicate != null)
                {
                    if (!predicate(e)) continue;
                }

                e.Draw(spriteBatch, editing, true);
            }
        }

        public static bool SaveCurrent(string filePath, MemoryStream previewImage = null)
        {
            if (MainSub == null)
            {
                MainSub = new Submarine(filePath);
            }

            MainSub.filePath = filePath;
            return MainSub.SaveAs(filePath, previewImage);
        }

        public void CreatePreviewWindow(GUIMessageBox messageBox)
        {
            new GUITextBlock(new RectTransform(new Vector2(1, 0), messageBox.Content.RectTransform, Anchor.TopCenter), Name, textAlignment: Alignment.Center, font: GUI.LargeFont, wrap: true);

            var upperPart = new GUIFrame(new RectTransform(new Vector2(1, 0.4f), messageBox.Content.RectTransform, Anchor.Center, Pivot.BottomCenter), color: Color.Transparent);
            var descriptionBox = new GUIFrame(new RectTransform(new Vector2(1, 0.2f), messageBox.Content.RectTransform, Anchor.Center, Pivot.TopCenter)
            {
                RelativeOffset = new Vector2(0, 0.1f),
            }, color: Color.Transparent);

            if (PreviewImage == null)
            {
                //var txtBlock = new GUITextBlock(new Rectangle(-20, 60, 256, 128), TextManager.Get("SubPreviewImageNotFound"), Color.Black * 0.5f, null, Alignment.Center, "", frame, true);
                //var txtBlock = new GUITextBlock(new RectTransform())
                //txtBlock.OutlineColor = txtBlock.TextColor;
                new GUITextBlock(new RectTransform(new Vector2(0.45f, 1), upperPart.RectTransform), TextManager.Get("SubPreviewImageNotFound"));
            }
            else
            {
                new GUIImage(new RectTransform(new Vector2(0.45f, 1), upperPart.RectTransform), PreviewImage);
            }

            Vector2 realWorldDimensions = Dimensions * Physics.DisplayToRealWorldRatio;
            string dimensionsStr = realWorldDimensions == Vector2.Zero ?
                TextManager.Get("Unknown") :
                TextManager.Get("DimensionsFormat").Replace("[width]", ((int)(realWorldDimensions.X)).ToString()).Replace("[height]", ((int)(realWorldDimensions.Y)).ToString());

            var layoutGroup = new GUILayoutGroup(new RectTransform(new Vector2(0.45f, 1), upperPart.RectTransform, Anchor.TopRight));

            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform), 
                $"{TextManager.Get("Dimensions")}: {dimensionsStr}",
                font: GUI.SmallFont, wrap: true);

            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                $"{TextManager.Get("RecommendedCrewSize")}: {(RecommendedCrewSizeMax == 0 ? TextManager.Get("Unknown") : RecommendedCrewSizeMin + " - " + RecommendedCrewSizeMax)}",
                font: GUI.SmallFont, wrap: true);

            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                $"{TextManager.Get("RecommendedCrewExperience")}: {(string.IsNullOrEmpty(RecommendedCrewExperience) ? TextManager.Get("unknown") : TextManager.Get(RecommendedCrewExperience))}",
                font: GUI.SmallFont, wrap: true);

            new GUITextBlock(new RectTransform(new Vector2(1, 0), layoutGroup.RectTransform),
                $"{TextManager.Get("RequiredContentPackages")}: {string.Join(", ", RequiredContentPackages)}", 
                font: GUI.SmallFont, wrap: true);

            new GUITextBlock(new RectTransform(new Vector2(1, 0), descriptionBox.RectTransform, Anchor.TopLeft), Description, font: GUI.SmallFont, wrap: true)
            {
                CanBeFocused = false
            };
        }

        public void CheckForErrors()
        {
            List<string> errorMsgs = new List<string>();

            if (!Hull.hullList.Any())
            {
                errorMsgs.Add(TextManager.Get("NoHullsWarning"));
            }

            foreach (Item item in Item.ItemList)
            {
                if (item.GetComponent<Items.Components.Vent>() == null) continue;

                if (!item.linkedTo.Any())
                {
                    errorMsgs.Add(TextManager.Get("DisconnectedVentsWarning"));
                    break;
                }
            }

            if (WayPoint.WayPointList.Find(wp => !wp.MoveWithLevel && wp.SpawnType == SpawnType.Path) == null)
            {
                errorMsgs.Add(TextManager.Get("NoWaypointsWarning"));
            }

            if (WayPoint.WayPointList.Find(wp => wp.SpawnType == SpawnType.Cargo) == null)
            {
                errorMsgs.Add(TextManager.Get("NoCargoSpawnpointWarning"));
            }

            if (!Item.ItemList.Any(it => it.GetComponent<Items.Components.Pump>() != null && it.HasTag("ballast")))
            {
                errorMsgs.Add(TextManager.Get("NoBallastTagsWarning"));
            }

            if (errorMsgs.Any())
            {
                new GUIMessageBox(TextManager.Get("Warning"), string.Join("\n\n", errorMsgs), 400, 0);
            }

            foreach (MapEntity e in MapEntity.mapEntityList)
            {
                if (Vector2.Distance(e.Position, HiddenSubPosition) > 20000)
                {
                    var msgBox = new GUIMessageBox(
                        TextManager.Get("Warning"),
                        TextManager.Get("FarAwayEntitiesWarning"),
                        new string[] { TextManager.Get("Yes"), TextManager.Get("No") });

                    msgBox.Buttons[0].OnClicked += (btn, obj) =>
                    {
                        GameMain.SubEditorScreen.Cam.Position = e.WorldPosition;
                        return true;
                    };
                    msgBox.Buttons[0].OnClicked += msgBox.Close;
                    msgBox.Buttons[1].OnClicked += msgBox.Close;

                    break;

                }
            }
        }
        
        public void ClientRead(ServerNetObject type, NetBuffer msg, float sendingTime)
        {
            var newTargetPosition = new Vector2(
                msg.ReadFloat(),
                msg.ReadFloat());
            
            //already interpolating with more up-to-date data -> ignore
            if (subBody.MemPos.Count > 1 && subBody.MemPos[0].Timestamp > sendingTime)
            {
                return;
            }

            int index = 0;
            while (index < subBody.MemPos.Count && sendingTime > subBody.MemPos[index].Timestamp)
            {
                index++;
            }

            //position with the same timestamp already in the buffer (duplicate packet?)
            //  -> no need to add again
            if (index < subBody.MemPos.Count && sendingTime == subBody.MemPos[index].Timestamp)
            {
                return;
            }
            
            subBody.MemPos.Insert(index, new PosInfo(newTargetPosition, 0.0f, sendingTime));
        }
    }
}
