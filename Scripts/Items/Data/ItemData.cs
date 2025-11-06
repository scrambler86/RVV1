using UnityEngine;

namespace Game.Items
{
    public enum ItemType
    {
        Equipment,
        Consumable,
        Quest,
        Currency,
        Misc
    }

    public enum EquipmentSlot
    {
        None,
        Warrior,
        Mage,
        Archer,
        Healer
    }

    public enum RarityType
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    [CreateAssetMenu(fileName = "NewItemData", menuName = "Game/Item Data")]
    public class ItemData : ScriptableObject
    {
        [Header("Info Base")]
        public string itemName;
        [TextArea] public string description;
        public Sprite icon;

        [Header("Tipo e Comportamento")]
        public ItemType itemType;
        public EquipmentSlot allowedSlot = EquipmentSlot.None;
        public RarityType rarity = RarityType.Common;

        [Header("Stack")]
        public bool stackable = false;
        [Min(1)] public int maxStack = 1;

        [Header("Prefab 3D")]
        public GameObject worldPrefab;

        [Header("ID Unico (assegnato a runtime)")]
        [HideInInspector] public string uniqueId;
    }
}
