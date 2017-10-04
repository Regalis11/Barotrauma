﻿using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace Barotrauma.Items.Components
{
    class RangedWeapon : ItemComponent
    {
        private float reload, reloadTimer;

        private Vector2 barrelPos;

        [HasDefaultValue("0.0,0.0", false)]
        public string BarrelPos
        {
            get { return XMLExtensions.Vector2ToString(ConvertUnits.ToDisplayUnits(barrelPos)); }
            set { barrelPos = ConvertUnits.ToSimUnits(XMLExtensions.ParseToVector2(value)); }
        }

        [HasDefaultValue(1.0f, false)]
        public float Reload
        {
            get { return reload; }
            set { reload = Math.Max(value, 0.0f); }
        }

        public Vector2 TransformedBarrelPos
        {
            get
            {
                Matrix bodyTransform = Matrix.CreateRotationZ(item.body.Rotation);
                Vector2 flippedPos = barrelPos;
                if (item.body.Dir < 0.0f) flippedPos.X = -flippedPos.X;
                return (Vector2.Transform(flippedPos, bodyTransform) + item.body.SimPosition);
            }
        }
                
        public RangedWeapon(Item item, XElement element)
            : base(item, element)
        {
            //barrelPos = ToolBox.GetAttributeVector2(element, "barrelpos", Vector2.Zero);
            //barrelPos = ConvertUnits.ToSimUnits(barrelPos);
        }

        public override void Update(float deltaTime, Camera cam)
        {
            reloadTimer -= deltaTime;

            if (reloadTimer < 0.0f)
            {
                reloadTimer = 0.0f;
                IsActive = false;
            }
        }
        
        public override bool Use(float deltaTime, Character character = null)
        {
            if (character == null) return false;
            if (!character.IsKeyDown(InputType.Aim) || reloadTimer > 0.0f) return false;
            IsActive = true;
            reloadTimer = reload;

            List<Body> limbBodies = new List<Body>();
            foreach (Limb l in character.AnimController.Limbs)
            {
                limbBodies.Add(l.body.FarseerBody);
            }

            float degreeOfFailure = (100.0f - DegreeOfSuccess(character))/100.0f;

            degreeOfFailure *= degreeOfFailure;

            if (degreeOfFailure > Rand.Range(0.0f, 1.0f))
            {
                ApplyStatusEffects(ActionType.OnFailure, 1.0f, character);
            }

            Item[] containedItems = item.ContainedItems;
            if (containedItems != null)
            {
                foreach (Item projectile in containedItems)
                {
                    if (projectile == null) continue;
                    //find the projectile-itemcomponent of the projectile,
                    //and add the limbs of the shooter to the list of bodies to be ignored
                    //so that the player can't shoot himself
                    Projectile projectileComponent= projectile.GetComponent<Projectile>();
                    if (projectileComponent == null) continue;

                    float rotation = ((item.body.Dir == 1.0f) ? item.body.Rotation : item.body.Rotation - MathHelper.Pi) + Rand.Range(-degreeOfFailure, degreeOfFailure);
                
                    projectile.body.ResetDynamics();
                    projectile.SetTransform(TransformedBarrelPos, rotation);

                    projectile.Use(deltaTime);
                    projectileComponent.User = character;

                    projectile.body.ApplyTorque(projectile.body.Mass * degreeOfFailure * Rand.Range(-10.0f, 10.0f));

                    //set the rotation of the projectile again because dropping the projectile resets the rotation
                    projectile.SetTransform(projectile.SimPosition, rotation);

                    //recoil
                    item.body.ApplyLinearImpulse(
                        new Vector2((float)Math.Cos(projectile.body.Rotation), (float)Math.Sin(projectile.body.Rotation)) * item.body.Mass * -50.0f);
                
                    projectileComponent.IgnoredBodies = limbBodies;

                    item.RemoveContained(projectile);
                
                    Rope rope = item.GetComponent<Rope>();
                    if (rope != null) rope.Attach(projectile);

                    return true;
                }
            }




            return true;      
        }
            
    }
}
