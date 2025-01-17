﻿using ItemChanger;
using ItemChanger.Tags;
using MultiWorldLib;
using MultiWorldMod.Items.Remote.UIDefs;
using Newtonsoft.Json;

namespace MultiWorldMod.Items.Remote.Tags
{
    internal class ReceivedItemTag : Tag, IInteropTag
    {
        public int Id = 0;
        public bool Given = false;
        
        public static readonly string FromFieldName = "From";
        public string From = null;

        [JsonIgnore] private AbstractItem parent;
        
        public string Message => "RemotelyPlacedItemTag";

        public override void Load(object parent)
        {
            this.parent = (AbstractItem)parent;
            this.parent.ModifyItem += WrapUIDefAsReceived;
        }

        public override void Unload(object parent)
        {
            this.parent.ModifyItem -= WrapUIDefAsReceived;
            this.parent = (AbstractItem)parent;
        }

        private static void WrapUIDefAsReceived(GiveEventArgs giveEventArgs)
        {
            string from = giveEventArgs.Orig.GetTag<ReceivedItemTag>().From;
            giveEventArgs.Item.UIDef = ReceivedItemUIDef.Convert(giveEventArgs.Item.UIDef, from);
        }

        public void GiveThisItem(AbstractPlacement placement, string from)
        {
            Given = true;
            From = from;

            parent.Give(placement, GetStandardGiveInfo());
        }

        public bool IdEquals(int id)
        {
            return id == Id || id == Consts.SERVER_GENERIC_ITEM_ID;
        }

        private GiveInfo GetStandardGiveInfo()
        {
            return new GiveInfo()
            {
                Container = "MultiWorld",
                FlingType = FlingType.DirectDeposit,
                MessageType = MessageType.Corner,
                Transform = null,
                Callback = null
            };
        }

        internal bool CanBeGiven()
        {
            return !Given || IsItemSomewhatPersistent();
        }

        private bool IsItemSomewhatPersistent()
        {
            return parent.GetTag(out IPersistenceTag tag) && tag.Persistence != Persistence.Single;
        }

        public bool TryGetProperty<T>(string propertyName, out T value)
        {
            if (propertyName == FromFieldName && From is T from)
            {
                value = from;
                return true;
            }
            value = default;
            return false;
        }
    }
}
