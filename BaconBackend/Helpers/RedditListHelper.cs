﻿using BaconBackend.Managers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BaconBackend.Helpers
{
    /// <summary>
    /// Helper classes used to help parse the Json.
    /// </summary>
    public class Element<Et>
    {
        [JsonProperty(PropertyName = "data")]
        public Et Data;

        [JsonProperty(PropertyName = "kind")]
        public string Kind;
    }

    /// <summary>
    /// Helper class that holds the list
    /// </summary>
    public class ElementList<Et>
    {
        [JsonProperty(PropertyName = "children")]
        public List<Element<Et>> Children;

        [JsonProperty(PropertyName = "after")]
        public string After = null;

        [JsonProperty(PropertyName = "before")]
        public string Before = null;
    }

    /// <summary>
    /// Holds the root information
    /// </summary>
    public class RootElement<Et>
    {
        [JsonProperty(PropertyName = "data")]
        public ElementList<Et> Data;

        [JsonProperty(PropertyName = "kind")]
        public string Kind;
    }

    /// <summary>
    /// Used for special case roots that have a nameless array.
    /// </summary>
    public class ArrayRoot<Et>
    {
        [JsonProperty(PropertyName = "root")]
        public List<RootElement<Et>> Root;
    }


    /// <summary>
    /// This is a helper class designed to help with returning long
    /// reddit lists. Most lists on reddit have a limit (like subreddits and such)
    /// and if you want the entire thing it might take multiple calls.
    /// </summary>
    class RedditListHelper<T>
    {
        //
        // Private vars
        //
        string m_baseUrl;
        string m_optionalGetArgs;
        NetworkManager m_networkMan;
        int m_lastTopGet = 0;
        ElementList<T> m_currentElementList = new ElementList<T>();

        /// <summary>
        /// This is interesting. For comments the json returned has a nameless json array in the beginning.
        /// Json.net can't handle it, so we must fix it up ourselves.
        /// </summary>
        bool m_hasEmptyArrayRoot = false;

        /// <summary>
        /// If we are creating an empty root as above, this tells us which element in the created root to use.
        /// </summary>
        bool m_takeFirstArrayRoot = false;

        public RedditListHelper(string baseUrl, NetworkManager netMan, bool hasEmptyArrayRoot = false, bool takeFirstArrayRoot = false, string optionalGetArgs = "")
        {
            m_baseUrl = baseUrl;
            m_optionalGetArgs = optionalGetArgs;
            m_currentElementList.Children = new List<Element<T>>();
            m_networkMan = netMan;
            m_hasEmptyArrayRoot = hasEmptyArrayRoot;
            m_takeFirstArrayRoot = takeFirstArrayRoot;
        }

        /// <summary>
        /// Fetches the next n number of elements from the source. If there aren't n left it will return how every
        /// many it can get. If the amount requested is more than what it current has it will try to fetch more.
        /// THIS IS NOT THREAD SAFE
        /// </summary>
        /// <param name="count">The number to get</param>
        /// <returns></returns>
        public async Task<List<Element<T>>> FetchNext(int count)
        {
            return await FetchElements(m_lastTopGet, m_lastTopGet + count);
        }

        /// <summary>
        /// Returns a range of elements from the source, if the elements are not local it will fetch them from the interwebs
        /// This can take multiple web calls to get the list, so this can be slow. If there aren't enough elements remaining
        /// we will return as many as we can get.
        /// THIS IS NOT THREAD SAFE
        /// </summary>
        /// <param name="bottom">The bottom range, inclusive</param>
        /// <param name="top">Teh top of the range, exclusive</param>
        /// <returns></returns>
        public async Task<List<Element<T>>> FetchElements(int bottom, int top)
        {
            if(top <= bottom)
            {
                throw new Exception("top can't be larger than bottom!");
            }

            int santyCheckCount = 0;
            while (true)
            {
                // See if we now have what they asked for, OR the list has elements but we don't have an after.
                // (this is the case when we have hit the end of the list)
                // #bug!?!? At some point I changed the children count in the after check to santyCheckCount == 0, but I can't remember why
                // and it breaks lists that have ends. There is some bug where something doesn't try to refresh or something...
                if (m_currentElementList.Children.Count >= top
                    || (m_currentElementList.Children.Count != 0 && m_currentElementList.After == null)
                    || (santyCheckCount > 25))
                {
                    // Return what they asked for capped at the list size
                    int length = top - bottom;
                    int listLength = m_currentElementList.Children.Count - bottom;
                    length = Math.Min(length, listLength);

                    // Set what the top was we returned.
                    m_lastTopGet = bottom + length;
                    return m_currentElementList.Children.GetRange(bottom, length);
                }

                // Figure out how many we need still.
                int numberNeeded = top - m_currentElementList.Children.Count;

                // Make the request.
                string webResult = await MakeRequest(numberNeeded, m_currentElementList.After);

                RootElement<T> root = null;

                // Special case, see the comment on the bool var
                if (m_hasEmptyArrayRoot)
                {
                    // Fix up the json returned.
                    string namedRootJson = "{\"root\": " + webResult + "}";
                    ArrayRoot<T> arrayRoot = await Task.Run(() => JsonConvert.DeserializeObject<ArrayRoot<T>>(namedRootJson));

                    if(m_takeFirstArrayRoot)
                    {
                        // Used for forcing a post to load in flipview.
                        root = arrayRoot.Root[0];
                    }
                    else
                    {
                        // Used for comments to ignore the post header
                        root = arrayRoot.Root[1];
                    }
                }
                else
                {
                    // Parse the Json
                    root = await Task.Run(() => JsonConvert.DeserializeObject<RootElement<T>>(webResult));
                }

                // Copy the new contents into the current cache
                m_currentElementList.Children.AddRange(root.Data.Children);

                // Update the before and after
                m_currentElementList.After = root.Data.After;
                m_currentElementList.Before = root.Data.Before;
                santyCheckCount++;
            }
        }

        /// <summary>
        /// Returns what elements the helper currently has without fetching more.
        /// THIS IS NOT THREAD SAFE
        /// </summary>
        /// <returns>Returns the elements</returns>
        public List<Element<T>> GetCurrentElements()
        {
            return m_currentElementList.Children;
        }

        /// <summary>
        /// Adds a fake element to the collection such as a user comment reply.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="element"></param>
        public void AddFakeElement(int pos, T element)
        {
            //m_currentElementList.Children.Insert(pos, element);
        }

        /// <summary>
        /// Clears the current list.
        /// </summary>
        public void Clear()
        {
            m_currentElementList.After = "";
            m_currentElementList.Before = "";
            m_currentElementList.Children.Clear();
        }

        private async Task<string> MakeRequest(int limit, string after)
        {
            string optionalEnding = String.IsNullOrWhiteSpace(m_optionalGetArgs) ? String.Empty : "&"+ m_optionalGetArgs;
            string url = m_baseUrl + $"?limit={limit}" + (String.IsNullOrWhiteSpace(after) ? "" : $"&after={after}") + optionalEnding;
            return await m_networkMan.MakeRedditGetRequest(url);
        }


    }
}
