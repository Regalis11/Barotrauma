using System;
using Barotrauma.Networking;
using Barotrauma.Particles;
using Barotrauma.Sounds;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    partial class Repairable : ItemComponent, IDrawableComponent
    {
        public GUIButton RepairButton { get; private set; }

        public GUIButton SabotageButton { get; private set; }

        private GUIProgressBar progressBar;

        private List<ParticleEmitter> particleEmitters = new List<ParticleEmitter>();
        //the corresponding particle emitter is active when the condition is within this range
        private List<Vector2> particleEmitterConditionRanges = new List<Vector2>();

        private SoundChannel repairSoundChannel;

        private string repairButtonText, repairingText;
        private string sabotageButtonText, sabotagingText;

        private FixActions requestStartFixAction;

        private bool QTESuccess;

        private float QTETimer;
        public float QTETime = 0.5f;
        private float QTECooldown;
        public float QTECooldownTime = 0.5f;

        public float FakeBrokenTimer;

        [Serialize("", false, description: "An optional description of the needed repairs displayed in the repair interface.")]
        public string Description
        {
            get;
            set;
        }

        public Vector2 DrawSize
        {
            //use the extents of the item as the draw size
            get { return Vector2.Zero; }
        }

        public override bool ShouldDrawHUD(Character character)
        {
            if (!HasRequiredItems(character, false) || character.SelectedConstruction != item) return false;
            return item.ConditionPercentage < RepairThreshold || character.IsTraitor && item.ConditionPercentage > MinSabotageCondition || (CurrentFixer == character && (!item.IsFullCondition || (character.IsTraitor && item.ConditionPercentage > MinSabotageCondition)));
        }

        partial void InitProjSpecific(XElement element)
        {
            CreateGUI();
            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "emitter":
                    case "particleemitter":
                        particleEmitters.Add(new ParticleEmitter(subElement));
                        float minCondition = subElement.GetAttributeFloat("mincondition", 0.0f);
                        float maxCondition = subElement.GetAttributeFloat("maxcondition", 100.0f);

                        if (maxCondition < minCondition)
                        {
                            DebugConsole.ThrowError("Invalid damage particle configuration in the Repairable component of " + item.Name + ". MaxCondition needs to be larger than MinCondition.");
                            float temp = maxCondition;
                            maxCondition = minCondition;
                            minCondition = temp;
                        }
                        particleEmitterConditionRanges.Add(new Vector2(minCondition, maxCondition));

                        break;
                }
            }
        }
        
        private void RecreateGUI()
        {
            if (GuiFrame != null)
            {
                GuiFrame.ClearChildren();
                CreateGUI();
            }
        }

        private void CreateGUI()
        {
            var paddedFrame = new GUILayoutGroup(new RectTransform(new Vector2(0.8f, 0.75f), GuiFrame.RectTransform, Anchor.Center), childAnchor: Anchor.TopCenter)
            {
                Stretch = true,
                RelativeSpacing = 0.05f,
                CanBeFocused = true
            };
            
            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.15f), paddedFrame.RectTransform),
                header, textAlignment: Alignment.TopCenter, font: GUI.LargeFont);

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedFrame.RectTransform),
                Description, font: GUI.SmallFont, wrap: true);

            new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedFrame.RectTransform),
                TextManager.Get("RequiredRepairSkills"), font: GUI.SubHeadingFont);
            for (int i = 0; i < requiredSkills.Count; i++)
            {
                var skillText = new GUITextBlock(new RectTransform(new Vector2(1.0f, 0.0f), paddedFrame.RectTransform),
                    "   - " + TextManager.AddPunctuation(':', TextManager.Get("SkillName." + requiredSkills[i].Identifier), ((int) Math.Round(requiredSkills[i].Level * SkillRequirementMultiplier)).ToString()),
                    font: GUI.SmallFont)
                {
                    UserData = requiredSkills[i]
                };
            }

            var progressBarHolder = new GUILayoutGroup(new RectTransform(new Vector2(1.0f, 0.2f), paddedFrame.RectTransform), isHorizontal: true)
            {
                Stretch = true,
                RelativeSpacing = 0.02f
            };

            progressBar = new GUIProgressBar(new RectTransform(new Vector2(0.6f, 1.0f), progressBarHolder.RectTransform),
                color: GUI.Style.Green, barSize: 0.0f, style: "DeviceProgressBar");

            QTETimer = QTETime;

            repairButtonText = TextManager.Get("RepairButton");
            repairingText = TextManager.Get("Repairing");
            RepairButton = new GUIButton(new RectTransform(new Vector2(0.4f, 1.0f), progressBarHolder.RectTransform, Anchor.TopCenter), repairButtonText)
            {
                OnClicked = (btn, obj) =>
                {
                    requestStartFixAction = FixActions.Repair;
                    item.CreateClientEvent(this);
                    return true;
                },
                OnButtonDown = () =>
                {
                    QTEAction();
                    return true;
                }
            };
            RepairButton.TextBlock.AutoScaleHorizontal = true;
            progressBarHolder.RectTransform.MinSize = RepairButton.RectTransform.MinSize;
            RepairButton.RectTransform.MinSize = new Point((int)(RepairButton.TextBlock.TextSize.X * 1.2f), RepairButton.RectTransform.MinSize.Y);

            sabotageButtonText = TextManager.Get("SabotageButton");
            sabotagingText = TextManager.Get("Sabotaging");
            SabotageButton = new GUIButton(new RectTransform(new Vector2(0.8f, 0.15f), paddedFrame.RectTransform, Anchor.BottomCenter), sabotageButtonText, style: "GUIButtonSmall")
            {
                IgnoreLayoutGroups = true,
                Visible = false,
                OnClicked = (btn, obj) =>
                {
                    requestStartFixAction = FixActions.Sabotage;
                    item.CreateClientEvent(this);
                    return true;
                },
                OnButtonDown = () =>
                {
                    QTEAction();
                    return true;
                }
            };
        }

        partial void UpdateProjSpecific(float deltaTime)
        {
            if (FakeBrokenTimer > 0.0f)
            {
                item.FakeBroken = true;
                if (Character.Controlled == null || (Character.Controlled.CharacterHealth.GetAffliction("psychosis")?.Strength ?? 0.0f) <= 0.0f)
                {
                    FakeBrokenTimer = 0.0f;
                }
                else
                {
                    FakeBrokenTimer -= deltaTime;
                }
            }
            else
            {
                item.FakeBroken = false;
            }


            if (!GameMain.IsMultiplayer)
            {
                switch (requestStartFixAction)
                {
                    case FixActions.Repair:
                    case FixActions.Sabotage:
                        StartRepairing(Character.Controlled, requestStartFixAction);
                        requestStartFixAction = FixActions.None;
                        break;
                    default:
                        requestStartFixAction = FixActions.None;
                        break;
                }
            }

            for (int i = 0; i < particleEmitters.Count; i++)
            {
                if ((item.ConditionPercentage >= particleEmitterConditionRanges[i].X && item.ConditionPercentage <= particleEmitterConditionRanges[i].Y) || FakeBrokenTimer > 0.0f)
                {
                    particleEmitters[i].Emit(deltaTime, item.WorldPosition, item.CurrentHull);
                }
            }

            if (CurrentFixer != null && CurrentFixer.SelectedConstruction == item)
            {
                if (repairSoundChannel == null || !repairSoundChannel.IsPlaying)
                {
                    repairSoundChannel = SoundPlayer.PlaySound("repair", item.WorldPosition, hullGuess: item.CurrentHull);
                }

                if (QTECooldown > 0.0f)
                {
                    QTECooldown -= deltaTime;
                    if (QTECooldown <= 0.0f)
                    {
                        QTETimer = QTETime;
                    }
                }
                else
                {
                    QTETimer -= deltaTime * (QTETimer / QTETime);
                    if (QTETimer < 0.0f) QTETimer = QTETime;
                
                }
            }
            else
            {
                repairSoundChannel?.FadeOutAndDispose();
                repairSoundChannel = null;
            }
        }
        
        public override void DrawHUD(SpriteBatch spriteBatch, Character character)
        {
            IsActive = true;

            progressBar.BarSize = item.Condition / item.MaxCondition;
            progressBar.Color = ToolBox.GradientLerp(progressBar.BarSize, GUI.Style.Red, GUI.Style.Orange, GUI.Style.Green);
            progressBar.Parent.Parent.Parent.DrawManually(spriteBatch, true);

            Rectangle sliderRect = progressBar.GetSliderRect(1.0f);
            GUI.DrawRectangle(spriteBatch,
                    new Rectangle(sliderRect.X + (int)((QTETimer / QTETime) * sliderRect.Width), sliderRect.Y - 5, 2, sliderRect.Height + 10),
                    QTECooldown <= 0.0f ? Color.White : QTESuccess ? Color.Green : Color.Red * 0.5f, true);

            RepairButton.Enabled = currentFixerAction == FixActions.None || (CurrentFixer == character && !item.IsFullCondition);
            RepairButton.Text = (currentFixerAction == FixActions.None || CurrentFixer != character || currentFixerAction != FixActions.Repair) ? 
                repairButtonText : 
                repairingText + new string('.', ((int)(Timing.TotalTime * 2.0f) % 3) + 1);

            SabotageButton.Visible = character.IsTraitor;
            SabotageButton.IgnoreLayoutGroups = !SabotageButton.Visible;
            SabotageButton.Enabled = (currentFixerAction == FixActions.None || (CurrentFixer == character && currentFixerAction != FixActions.Sabotage)) && character.IsTraitor && item.ConditionPercentage > MinSabotageCondition;
            SabotageButton.Text = (currentFixerAction == FixActions.None || CurrentFixer != character || currentFixerAction != FixActions.Sabotage || !character.IsTraitor) ?
                sabotageButtonText :
                sabotagingText + new string('.', ((int)(Timing.TotalTime * 2.0f) % 3) + 1);

            System.Diagnostics.Debug.Assert(GuiFrame.GetChild(0) is GUILayoutGroup, "Repair UI hierarchy has changed, could not find skill texts");
            foreach (GUIComponent c in GuiFrame.GetChild(0).Children)
            {
                if (!(c.UserData is Skill skill)) continue;

                GUITextBlock textBlock = (GUITextBlock)c;
                if (character.GetSkillLevel(skill.Identifier) < (skill.Level * SkillRequirementMultiplier))
                {
                    textBlock.TextColor = GUI.Style.Red;
                }
                else
                {
                    textBlock.TextColor = Color.White;
                }
            }
        }

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1)
        {
            if (GameMain.DebugDraw && Character.Controlled?.FocusedItem == item)
            {
                bool paused = !ShouldDeteriorate();
                if (deteriorationTimer > 0.0f)
                {
                    GUI.DrawString(spriteBatch,
                        new Vector2(item.WorldPosition.X, -item.WorldPosition.Y), "Deterioration delay " + ((int)deteriorationTimer) + (paused ? " [PAUSED]" : ""),
                        paused ? Color.Cyan : Color.Lime, Color.Black * 0.5f);
                }
                else
                {
                    GUI.DrawString(spriteBatch,
                        new Vector2(item.WorldPosition.X, -item.WorldPosition.Y), "Deteriorating at " + (int)(DeteriorationSpeed * 60.0f) + " units/min" + (paused ? " [PAUSED]" : ""),
                        paused ? Color.Cyan : GUI.Style.Red, Color.Black * 0.5f);
                }
                GUI.DrawString(spriteBatch,
                    new Vector2(item.WorldPosition.X, -item.WorldPosition.Y + 20), "Condition: " + (int)item.Condition + "/" + (int)item.MaxCondition,
                    GUI.Style.Orange);
            }
        }

        protected override void RemoveComponentSpecific()
        {
            base.RemoveComponentSpecific();
            repairSoundChannel?.FadeOutAndDispose();
            repairSoundChannel = null;
        }

        private void QTEAction()
        {
            if (currentFixerAction == FixActions.Repair)
            {
                QTESuccess = QTECooldown <= 0.0f && QTETimer / QTETime <= item.Condition / item.MaxCondition;
            }
            else
            {
                return;
            }

            if (!GameMain.IsMultiplayer)RepairBoost(QTESuccess);

            //on failure during cooldown reset cursor to beginning
            if (!QTESuccess && QTECooldown > 0.0f) QTETimer = QTETime;
            QTECooldown = QTECooldownTime;
            //this will be set on button down so we can reset it here
            requestStartFixAction = FixActions.None;
            item.CreateClientEvent(this);
        }

        public void ClientRead(ServerNetObject type, IReadMessage msg, float sendingTime)
        {
            deteriorationTimer = msg.ReadSingle();
            deteriorateAlwaysResetTimer = msg.ReadSingle();
            DeteriorateAlways = msg.ReadBoolean();
            ushort currentFixerID = msg.ReadUInt16();
            currentFixerAction = (FixActions)msg.ReadRangedInteger(0, 2);
            CurrentFixer = currentFixerID != 0 ? Entity.FindEntityByID(currentFixerID) as Character : null;
            repairBoost = msg.ReadSingle();
        }

        public void ClientWrite(IWriteMessage msg, object[] extraData = null)
        {
            msg.WriteRangedInteger((int)requestStartFixAction, 0, 2);
            msg.Write(QTESuccess);
        }
    }
}
