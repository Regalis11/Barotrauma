﻿using System.Collections.Generic;
using System;
using System.Linq;

namespace Barotrauma
{
    class TaskManager
    {
        const float CriticalPriority = 50.0f;

        private List<Task> tasks;

        //private GUIListBox taskListBox;

        public List<Task> Tasks
        {
            get { return tasks; }
        }

        public bool CriticalTasks
        {
            get
            {
                return tasks.Any(task => task.Priority >= CriticalPriority);
            }
        }

        public TaskManager(GameSession session)
        {
            tasks = new List<Task>();

            //taskListBox = new GUIListBox(new Rectangle(Game1.GraphicsWidth - 250, 50, 250, 500), Color.Transparent);
            //taskListBox.ScrollBarEnabled = false;
            //taskListBox.Padding = GUI.style.smallPadding;           
        }

        public void AddTask(Task newTask)
        {
            if (tasks.Contains(newTask)) return;
            
            tasks.Add(newTask);
        }

        public void StartShift(Level level)
        {
            CreateScriptedEvents(level);

            //taskListBox.ClearChildren();
        }


        public void EndShift()
        {
            //taskListBox.ClearChildren();
            tasks.Clear();
        }

        private void CreateScriptedEvents(Level level)
        {
            MTRandom rand = new MTRandom(ToolBox.StringToInt(level.Seed));

            float totalDifficulty = level.Difficulty;

            int tries = 0;
            while (tries < 5)
            {
                ScriptedEvent scriptedEvent = ScriptedEvent.LoadRandom(rand);
                if (scriptedEvent==null || scriptedEvent.Difficulty > totalDifficulty)
                {
                    tries++;
                    continue;
                }
                DebugConsole.Log("Created scripted event " + scriptedEvent.ToString());

                AddTask(new ScriptedTask(scriptedEvent));
                totalDifficulty -= scriptedEvent.Difficulty;
                tries = 0;
            } 

        }
        
        public void TaskFinished(Task task)
        {

        }



        public void Update(float deltaTime)
        {
            Task removeTask = null;
            foreach (Task task in tasks)
            {
                if (task.IsFinished)
                {                    
                    //foreach (GUIComponent comp in taskListBox.children)
                    //{
                    //    if (comp.UserData as Task != task) continue;
                    //    comp.Rect = new Rectangle(comp.Rect.X, comp.Rect.Y, comp.Rect.Width, comp.Rect.Height - 1);
                    //    comp.children[0].ClearChildren();
                    //    if (comp.Rect.Height < 1)
                    //    {
                    //        removeComponent = comp;
                            removeTask = task;
                    //    }
                    //    break;
                    //}

                }
                else
                {
                    task.Update(deltaTime);
                }
            }

            if (removeTask!= null)
            {
                //taskListBox.RemoveChild(removeComponent);
                tasks.Remove(removeTask);
            }

            //endShiftButton.Enabled = finished || Game1.server!=null;
        }

    }
}
