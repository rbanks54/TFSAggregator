﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Text;
using TFSAggregator.TfsFacade;
using TFS = Microsoft.TeamFoundation.WorkItemTracking.Client;
using TFSAggregator.TfsSpecific;

namespace TFSAggregator
{
    public static class TfsWorkItemExtensions
    {
        /// <summary>
        /// Gets the workItem's parent from the list (if it is in there) or it will load
        /// it from the tfs store.  We want to use the list if we can so that we can save only
        /// one time.  (if not we could get conflicts in TFS for several updates to the same item.
        /// </summary>
        public static IWorkItem GetParentFromListOrStore(this IWorkItem childItem, IEnumerable<IWorkItem> workItemList, IWorkItemRepository store)
        {
            IWorkItem parent;

            // See if the parent work item is already in our workItemList (if so just use that one).
            int targetWorkItemId = (from TFS.WorkItemLink workItemLink in childItem.WorkItemLinks
                                    where workItemLink.LinkTypeEnd.Name == "Parent"
                                    select workItemLink.TargetId).FirstOrDefault();

            parent = workItemList.Where(workItem => workItem.Id == targetWorkItemId).FirstOrDefault();

            if (parent == null)
            {
                parent = (from TFS.WorkItemLink workItemLink in childItem.WorkItemLinks
                                  where workItemLink.LinkTypeEnd.Name == "Parent"
                                  select store.GetWorkItem(workItemLink.TargetId)).FirstOrDefault();
            }

            return parent;
        }

        /// <summary>
        /// Gets the children of a work item
        /// </summary>
        public static IEnumerable<IWorkItem> GetChildrenFromListOrStore(this IWorkItem parent, IEnumerable<IWorkItem> workItemList, IWorkItemRepository store)
        {
            var decendants = new List<IWorkItem>();

            // Go through all the links for the work item passed in (parent)
            foreach (TFS.WorkItemLink link in parent.WorkItemLinks)
            {
                IWorkItem childWorkItem;
                // Find all the child links
                if (link.LinkTypeEnd.Name == "Child")
                {
                    // If this child link is in the list of items then add it to the result set
                    if (workItemList.Select(x=>x.Id).Contains(link.TargetId))
                    {
                        var linkClosure = link;
                        childWorkItem = workItemList.Where(x => x.Id == linkClosure.TargetId).FirstOrDefault();
                    }
                    // if the item was not in our list then get it from the store
                    else
                    {
                        childWorkItem = store.GetWorkItem(link.TargetId);
                    }

                    decendants.Add(childWorkItem);
                }
            }
            
            return decendants;
        }

        public static void TransitionToState(this TFSAggregator.TfsFacade.IWorkItem workItem, string state, string commentPrefix)
        {
            // Set the sourceWorkItem's state so that it is clear that it has been moved.
            string originalState = (string)workItem.Fields["State"].Value;

            // Try to set the state of the source work item to the "Deleted/Moved" state (whatever is defined in the file).

            // We need an open work item to set the state
            workItem.TryOpen();

            // See if we can go directly to the planned state.
            workItem.Fields["State"].Value = state;


            if (workItem.Fields["State"].Status != TFS.FieldStatus.Valid)
            {
                // Revert back to the orginal value and start searching for a way to our "MovedState"
                workItem.Fields["State"].Value = workItem.Fields["State"].OriginalValue;

                // If we can't then try to go from the current state to another state.  Saving each time till we get to where we are going.
                foreach (string curState in workItem.FindNextState((string)workItem.Fields["State"].Value, state))
                {
                    string comment;
                    if (curState == state)
                        comment = commentPrefix + Environment.NewLine + "  State changed to " + state;
                    else
                        comment = commentPrefix + Environment.NewLine + "  State changed to " + curState + " as part of move toward a state of " + state;

                    bool success = ChangeWorkItemState(workItem, originalState, curState, comment);
                    // If we could not do the incremental state change then we are done.  We will have to go back to the orginal...
                    if (!success)
                        break;
                }
            }
            else
            {
                // Just save it off if we can.
                string comment = commentPrefix + "\n   State changed to " + state;
                ChangeWorkItemState(workItem, originalState, state, comment);

            }

        }
        
        private static bool ChangeWorkItemState(this IWorkItem workItem, string orginalSourceState, string destState, String comment)
        {
            // Try to save the new state.  If that fails then we also go back to the orginal state.
            try
            {
                workItem.TryOpen();
                workItem.Fields["State"].Value = destState;
                workItem.History = comment;

                if(TFSAggregatorSettings.LoggingIsEnabled) MiscHelpers.LogMessage(String.Format("{0}{0}{0}{0}{0}Attempting to move {1} [{2}] from {3} state to {4} state. (ChangeWorkItemState)", "  ", workItem.TypeName, workItem.Id, orginalSourceState, destState));
                if (workItem.IsValid())
                {
                    if (TFSAggregatorSettings.LoggingIsEnabled) MiscHelpers.LogMessage(String.Format("{0}{0}{0}{0}{0}{0}{1} [{2}] is valid to save.", "  ", workItem.TypeName, workItem.Id));
                }
                else
                {
                    if (TFSAggregatorSettings.LoggingIsEnabled) MiscHelpers.LogMessage(String.Format("{0}{0}{0}{0}{0}{0}The work item is invalid in the {1} state. Invalid fields: {2}", "  ", destState, MiscHelpers.GetInvalidWorkItemFieldsList(workItem).ToString()));
                }

                workItem.Save();
                return true;
            }
            catch (Exception)
            {
                // Revert back to the original value.
                workItem.Fields["State"].Value = orginalSourceState;
                return false;
            }
        }

        /// <summary>
        /// Used to find the next state on our way to a destination state.
        /// (Meaning if we are going from a "Not-Started" to a "Done" state, 
        /// we usually have to hit a "in progress" state first.
        /// </summary>
        /// <param name="wiType"></param>
        /// <param name="fromState"></param>
        /// <param name="toState"></param>
        /// <returns></returns>
        public static IEnumerable<string> FindNextState(this IWorkItem workItem, string fromState, string toState)
        {
            var map = new Dictionary<string, string>();
            var edges = workItem.GetTransitions().ToDictionary(i => i.From, i => i.To);
            var q = new Queue<string>();
            map.Add(fromState, null);
            q.Enqueue(fromState);
            while (q.Count > 0)
            {
                var current = q.Dequeue();
                foreach (var s in edges[current])
                {
                    if (!map.ContainsKey(s))
                    {
                        map.Add(s, current);
                        if (s == toState)
                        {
                            var result = new Stack<string>();
                            var thisNode = s;
                            do
                            {
                                result.Push(thisNode);
                                thisNode = map[thisNode];
                            } while (thisNode != fromState);
                            while (result.Count > 0)
                                yield return result.Pop();
                            yield break;
                        }
                        q.Enqueue(s);
                    }
                }
            }
            // no path exists
        }

        private static readonly Dictionary<string, IEnumerable<Transition>> _allTransistions = new Dictionary<string, IEnumerable<Transition>>();

        /// <summary>
        /// Deprecated
        /// Get the transitions for this <see cref="WorkItemType"/>
        /// </summary>
        /// <param name="workItemType"></param>
        /// <returns></returns>
        public static IEnumerable<Transition> GetTransitions(this IWorkItem workItem)
        {
            var wi = workItem as WorkItemWrapper;
            var wiType = wi.Type;

            IEnumerable<Transition> currentTransistions;

            // See if this WorkItemType has already had it's transistions figured out.
            _allTransistions.TryGetValue(workItem.TypeName, out currentTransistions);
            if (currentTransistions != null)
                return currentTransistions;

            // Get this worktype type as xml
            var workItemTypeXml = wiType.Export(false);

            // Create a dictionary to allow us to look up the "to" state using a "from" state.
            var newTransistions = new List<Transition>();

            // get the transistions node.
            var transitionsList = workItemTypeXml.GetElementsByTagName("TRANSITIONS");

            // As there is only one transistions item we can just get the first
            var transitions = transitionsList[0];

            // Iterate all the transitions
            foreach (XmlNode transitionXML in transitions)
            {
                // See if we have this from state already.
                string fromState = transitionXML.Attributes["from"].Value;
                Transition transition = newTransistions.Find(trans => trans.From == fromState);
                if (transition != null)
                {
                    transition.To.Add(transitionXML.Attributes["to"].Value);
                }
                // If we could not find this state already then add it.
                else
                {
                    // save off the transistion (from first so we can look up state progression.
                    newTransistions.Add(new Transition
                    {
                        From = transitionXML.Attributes["from"].Value,
                        To = new List<string> { transitionXML.Attributes["to"].Value }
                    });
                }
            }

            // Save off this transition so we don't do it again if it is needed.
            _allTransistions.Add(workItem.TypeName, newTransistions);

            return newTransistions;
        }

    }
}