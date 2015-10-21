﻿using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Barotrauma
{
    public enum Gender { None, Male, Female };        

    class CharacterInfo
    {
        public string Name;

        public Character Character;

        public readonly string File;
        
        public Job Job;

        private List<ushort> pickedItems;

        private Vector2[] headSpriteRange;

        private Gender gender;

        public int Salary;

        public int HeadSpriteId;
        private Sprite headSprite;

        public bool StartItemsGiven;

        public List<ushort> PickedItemIDs
        {
            get { return pickedItems; }
        }
        
        public Sprite HeadSprite
        {
            get
            {
                if (headSprite == null) LoadHeadSprite();
                return headSprite;
            }
        }

        public Gender Gender
        {
            get { return gender; }
            set
            {
                if (gender == value) return;
                gender = value;

                int genderIndex = (this.gender == Gender.Female) ? 1 : 0;
                if (headSpriteRange[genderIndex] != Vector2.Zero)
                {
                    HeadSpriteId = Rand.Range((int)headSpriteRange[genderIndex].X, (int)headSpriteRange[genderIndex].Y + 1);
                }
                else
                {
                    HeadSpriteId = 0;
                }

                LoadHeadSprite();
            }
        }

        public CharacterInfo(string file, string name = "", Gender gender = Gender.None, JobPrefab jobPrefab = null)
        {
            this.File = file;

            headSpriteRange = new Vector2[2];

            pickedItems = new List<ushort>();

            //ID = -1;

            XDocument doc = ToolBox.TryLoadXml(file);
            if (doc == null) return;

            if (ToolBox.GetAttributeBool(doc.Root, "genders", false))
            {
                if (gender == Gender.None)
                {
                    float femaleRatio = ToolBox.GetAttributeFloat(doc.Root, "femaleratio", 0.5f);
                    this.gender = (Rand.Range(0.0f, 1.0f, false) < femaleRatio) ? Gender.Female : Gender.Male;
                }
                else
                {
                    this.gender = gender;
                }
            }
                       
            headSpriteRange[0] = ToolBox.GetAttributeVector2(doc.Root, "headid", Vector2.Zero);
            headSpriteRange[1] = headSpriteRange[0];
            if (headSpriteRange[0] == Vector2.Zero)
            {
                headSpriteRange[0] = ToolBox.GetAttributeVector2(doc.Root, "maleheadid", Vector2.Zero);
                headSpriteRange[1] = ToolBox.GetAttributeVector2(doc.Root, "femaleheadid", Vector2.Zero);
            }

            int genderIndex = (this.gender == Gender.Female) ? 1 : 0;
            if (headSpriteRange[genderIndex] != Vector2.Zero)
            {
                HeadSpriteId = Rand.Range((int)headSpriteRange[genderIndex].X, (int)headSpriteRange[genderIndex].Y + 1);
            }

            this.Job = (jobPrefab == null) ? Job.Random() : new Job(jobPrefab);            

            if (!string.IsNullOrEmpty(name))
            {
                this.Name = name;
                return;
            }

            if (doc.Root.Element("name") != null)
            {
                string firstNamePath = ToolBox.GetAttributeString(doc.Root.Element("name"), "firstname", "");
                if (firstNamePath != "")
                {
                    firstNamePath = firstNamePath.Replace("[GENDER]", (this.gender == Gender.Female) ? "f" : "");
                    this.Name = ToolBox.GetRandomLine(firstNamePath);
                }

                string lastNamePath = ToolBox.GetAttributeString(doc.Root.Element("name"), "lastname", "");
                if (lastNamePath != "")
                {
                    lastNamePath = lastNamePath.Replace("[GENDER]", (this.gender == Gender.Female) ? "f" : "");
                    if (this.Name != "") this.Name += " ";
                    this.Name += ToolBox.GetRandomLine(lastNamePath);
                }
            }
            
            Salary = CalculateSalary();
        }

        private void LoadHeadSprite()
        {
            XDocument doc = ToolBox.TryLoadXml(File);
            if (doc == null) return;

            XElement ragdollElement = doc.Root.Element("ragdoll");
            foreach (XElement limbElement in ragdollElement.Elements())
            {
                if (ToolBox.GetAttributeString(limbElement, "type", "").ToLower() != "head") continue;

                XElement spriteElement = limbElement.Element("sprite");

                string spritePath = spriteElement.Attribute("texture").Value;

                spritePath = spritePath.Replace("[GENDER]", (this.gender == Gender.Female) ? "f" : "");
                spritePath = spritePath.Replace("[HEADID]", HeadSpriteId.ToString());
                
                headSprite = new Sprite(spriteElement, "", spritePath);
                break;
            }
        }
        
        public GUIFrame CreateInfoFrame(Rectangle rect)
        {
            GUIFrame frame = new GUIFrame(rect, Color.Transparent);
            frame.Padding = new Vector4(10.0f,10.0f,10.0f,10.0f);

            return CreateInfoFrame(frame);
        }

        public GUIFrame CreateInfoFrame(GUIFrame frame)
        {
            GUIImage image = new GUIImage(new Rectangle(0,0,30,30), HeadSprite, Alignment.TopLeft, frame);

            int x = 0, y = 0;
            new GUITextBlock(new Rectangle(x+80, y, 200, 20), Name, GUI.Style, frame);
            y += 20;

            if (Job!=null)
            {
                new GUITextBlock(new Rectangle(x+80, y, 200, 20), Job.Name, GUI.Style, frame);
                y += 30;

                var skills = Job.Skills;
                skills.Sort((s1, s2) => -s1.Level.CompareTo(s2.Level));

                new GUITextBlock(new Rectangle(x, y, 200, 20), "Skills:", GUI.Style, frame);
                y += 20;
                foreach (Skill skill in skills)
                {
                    Color textColor = Color.White * (0.5f + skill.Level/200.0f);
                    new GUITextBlock(new Rectangle(x+20, y, 200, 20), skill.Name, Color.Transparent, textColor, Alignment.Left, GUI.Style, frame);
                    new GUITextBlock(new Rectangle(x + 20, y, 200, 20), skill.Level.ToString(), Color.Transparent, textColor, Alignment.Right, GUI.Style, frame);
                    y += 20;
                }
            }


            return frame;
        }

        public void UpdateCharacterItems()
        {
            pickedItems.Clear();
            foreach (Item item in Character.Inventory.items)
            {
                if (item == null) continue;
                pickedItems.Add(item.ID);
            }
        }

        public CharacterInfo(XElement element)
        {
            Name = ToolBox.GetAttributeString(element, "name", "unnamed");

            string genderStr = ToolBox.GetAttributeString(element, "gender", "male").ToLower();
            gender = (genderStr == "m") ? Gender.Male : Gender.Female;

            File            = ToolBox.GetAttributeString(element, "file", "");
            Salary          = ToolBox.GetAttributeInt(element, "salary", 1000);
            HeadSpriteId    = ToolBox.GetAttributeInt(element, "headspriteid", 1);
            StartItemsGiven = ToolBox.GetAttributeBool(element, "startitemsgiven", false);

            pickedItems = new List<ushort>();

            string pickedItemString = ToolBox.GetAttributeString(element, "items", "");
            if (!string.IsNullOrEmpty(pickedItemString))
            {
                string[] itemIds = pickedItemString.Split(',');
                foreach (string s in itemIds)
                {
                    pickedItems.Add((ushort)int.Parse(s));
                }
            }

            foreach (XElement subElement in element.Elements())
            {
                if (subElement.Name.ToString().ToLower() != "job") continue;

                Job = new Job(subElement);
                break;
            }
        }

        private int CalculateSalary()
        {
            if (Name == null || Job == null) return 0;

            int salary = Math.Abs(Name.GetHashCode()) % 100;

            foreach (Skill skill in Job.Skills)
            {
                salary += skill.Level * 10;
            }

            return salary;
        }

        public virtual XElement Save(XElement parentElement)
        {
            XElement charElement = new XElement("character");

            charElement.Add(
                new XAttribute("name", Name),
                new XAttribute("file", File),
                new XAttribute("gender", gender == Gender.Male ? "m" : "f"),
                new XAttribute("salary", Salary),
                new XAttribute("headspriteid", HeadSpriteId),
                new XAttribute("startitemsgiven", StartItemsGiven));

            if (Character != null && Character.Inventory != null)
            {
                UpdateCharacterItems();
            }
            
            if (pickedItems.Count > 0)
            {
                charElement.Add(new XAttribute("items", string.Join(",", pickedItems)));
            }

            Job.Save(charElement);

            parentElement.Add(charElement);
            return charElement;
        }
    }
}
