using System;
using System.Collections.Generic;
using System.Data.SQLite;
using Mirror;
using TMPro;
using UnityEngine;

public class InventoryManager : NetworkBehaviour
{
    public InventorySlot[] inventorySlots;
    public GameObject inventoryItemPrefab;

    public GameObject mainInventory;
    public TextMeshProUGUI interactionText;
    [HideInInspector] public Player player;

    public Item inHands { get; private set; }
    public int pickupRange = 3;
    [HideInInspector] public int selectedSlot { get; private set; }

    // Communications forms
    
    public enum InvAction
    {
        Drop,
        Pickup,
        Give
    }
    
    public struct RequestInventoryAction : NetworkMessage
    {
        public InvAction action;
        public NetworkIdentity identity;
        public int id;
    }

    public struct ResponseInventoryAction : NetworkMessage
    {
        public bool success;
        public NetworkIdentity identity;
        public InvAction action;
    }
    
    public struct SyncInventory : NetworkMessage
    {
        public string inventory;
    }

    [Client]
    public void Enable()
    {
        if (!isLocalPlayer) return;
        UIReference uiReference = GameObject.FindGameObjectWithTag("Main UI").GetComponent<UIReference>();
        mainInventory = uiReference.mainInventoryGroup;
        interactionText = uiReference.interaction.GetComponent<TextMeshProUGUI>();
        FetchInventory();
        inventorySlots[selectedSlot].Select();
        ImportInventory(netIdentity);
    }

    [Client]
    private void FetchInventory() // Initialise inventory's UI
    {
        List<InventorySlot> inv = new List<InventorySlot>();
        GameObject mainInvUI = GameObject.FindGameObjectWithTag("Main UI").GetComponent<UIReference>().mainInventory;
        GameObject toolbarUI = GameObject.FindGameObjectWithTag("Main UI").GetComponent<UIReference>().toolBar;
        
        for (int i = 0; i < toolbarUI.transform.childCount; i++)
        {
            inv.Add(toolbarUI.transform.GetChild(i).GetComponent<InventorySlot>());
        }

        for (int i = 0; i < mainInvUI.transform.childCount; i++)
        {
            inv.Add(mainInvUI.transform.GetChild(i).GetComponent<InventorySlot>());
        }

        inventorySlots = inv.ToArray();
    }
    
    // Server - Client communication
    
    [Server]
    public void OnInventoryAction(NetworkConnection conn, RequestInventoryAction msg)
    {
        bool success = msg.action switch
        {
            InvAction.Drop => ServerItemDrop(conn, msg.identity),
            InvAction.Pickup => ServerItemPickup(conn, msg.identity),
            InvAction.Give => ServerGiveItem(conn, msg.id, 1, out msg.identity),
            _ => false
        };
        ResponseInventoryAction response = new ResponseInventoryAction()
                { success = success, action = msg.action, identity = msg.identity };
        
        conn.Send(response);
    }
    
    [Server]
    private bool ServerItemDrop(NetworkConnection conn, NetworkIdentity target)
    {
        Player connPlayer = conn.identity.GetComponent<Player>();
        Item item = ConvertFromNetID(target);
        if (item is not null && connPlayer.inventory.Exists(data => data.netIdentity.netId == target.netId)) // Player has the item
        {
            connPlayer.inventory.Remove(item);
            
            Vector3 spawnPos = connPlayer.transform.position + connPlayer.transform.forward * 2;
            
            // Make the object appear on the map
            Transform tranformOnGround = item.refOnGround.transform;
            item.NewParent(null);
            item.transform.position = spawnPos;
            item.VisibleOnGround(true); 
            return true;
        }

        return false;
    }

    [Server]
    private bool ServerItemPickup(NetworkConnection conn, NetworkIdentity target)
    {
        Player connPlayer = conn.identity.GetComponent<Player>();
        Item item = ConvertFromNetID(target);
        
        if (item is not null) // Check if pickable
        {
            if (item.CanBePickedUp && Vector3.Distance(connPlayer.transform.position, target.transform.position) < 5) // Check if player is near the item
            {
                (bool possible, Item newItem) = connPlayer.inventoryManager.GetItemAdd(item, item.Quantity);
                if (possible)
                {
                    if (newItem is not null)
                    {
                        connPlayer.inventory.Find(data => newItem.netIdentity == data.netIdentity).Quantity +=
                            newItem.Quantity;
                        NetworkServer.Destroy(newItem.gameObject);
                    }
                    else
                    {
                        item.VisibleOnGround(false);
                        item.NewParent(conn.identity);
                        connPlayer.inventory.Add(item);
                    }
                }

                return possible;
            }
        }

        return false;
    }

    [Server]
    private bool ServerGiveItem(NetworkConnection conn, int id, int quantity, out NetworkIdentity newIdentity)
    {
        GameObject nitem = NetManager.Instance.spawnPrefabs[id];
        Item item = nitem.GetComponent<Item>();
        Player connPlayer = conn.identity.GetComponent<Player>();
        (bool possible, Item newItem) = connPlayer.inventoryManager.GetItemAdd(item, quantity);
        newIdentity = null;
        if (possible) // Check if inventory is not full
        {
            if (newItem is not null)
            {
                newItem.Quantity += newItem.Quantity;
                newIdentity = newItem.netIdentity;
            }
            else
            {
                GameObject newGameObject = Instantiate(nitem);
                NetworkServer.Spawn(newGameObject);
                newItem = newGameObject.GetComponent<Item>();
                connPlayer.inventory.Add(newItem);
                newItem.VisibleOnGround(false);
                newItem.NewParent(connPlayer.netIdentity);
                newIdentity = newGameObject.GetComponent<NetworkIdentity>();
            }
        }
        return possible;
    }

    [Client]
    public void InventoryInteraction(NetworkIdentity target, InvAction action)
    {
        RequestInventoryAction message = new RequestInventoryAction();
        message.identity = target;
        message.action = action;
        NetworkClient.Send(message);
    }

    [Client]
    public void InventoryGive(int target)
    {
        RequestInventoryAction message = new RequestInventoryAction();
        message.id = target;
        message.action = InvAction.Give;
        NetworkClient.Send(message);
    }
    
    [Client]
    public void OnInventoryResponse(ResponseInventoryAction response)
    {
        Item item = ConvertFromNetID(response.identity);
        if (response.success)
        {
            if (response.action == InvAction.Pickup)
            {
                PickupItem(item);
            }
            else if (response.action == InvAction.Drop)
            {
                DropItem(FindItem(item));
            }
            else if (response.action == InvAction.Give)
            {
                PickupItem(item);
            }
        }
    }
    
    // Inventory managing
    [Server]
    public (bool, Item) GetItemAdd(Item target, int quantity)
    {
        for (int i = 0; i < player.inventory.Count; i++) // Add into items
        {
            Item item = player.inventory[i];
            if (item != null && item.UID == target.UID && quantity < item.maxStack)
            {
                return (true, item);
            }
        }

        return (player.inventory.Count <= player.invSize, null); // Need to create a new item
    }
    
    [Client]
    public void ChangeSelectedSlot(int newSelSlot)
    {
        inventorySlots[selectedSlot].Deselect();

        inventorySlots[newSelSlot].Select();
        selectedSlot = newSelSlot;
    }

    [Client]
    public void WeaponInHands()
    {
        inHands = GetSelectedItem(false);
        if (inHands is not null)
        {
            if (inHands.type == ItemType.Weapon)
            {
                player.playerWeapon.ChangeVisible(inHands.UID);
                WeaponData weaponData = inHands.GetComponent<WeaponData>();
                player.playerShoot.damage = weaponData.ammo.Damage;
                player.playerShoot.range = weaponData.ammo.AmmoRange;
                return;
            }
        }

        player.playerWeapon.HideWeapon();
        player.playerShoot.damage = 10;
        player.playerShoot.range = 1;
    }

    [Client]
    public void PickupItem(Item item)
    {
        AddItem(item);
    }

    [Client]
    public void SwitchInventory()
    {
        mainInventory.SetActive(!mainInventory.activeInHierarchy);
        player.playerController.inInterface = mainInventory.activeInHierarchy;

        if (mainInventory.activeInHierarchy)
        {
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    [Client]
    public bool AddItem(Item item)
    {
        // Add an object into the player's inventory ui
        for (int i = 0; i < inventorySlots.Length; i++)
        {
            InventorySlot slot = inventorySlots[i];
            InventoryItem itemInSlot = slot.GetComponentInChildren<InventoryItem>();
            if (itemInSlot is not null && itemInSlot.item == item)
            {
                itemInSlot.RefreshCount();
                return true;
            }
        }
        for (int i = 0; i < inventorySlots.Length; i++)
        {
            InventorySlot slot = inventorySlots[i];
            InventoryItem itemInSlot = slot.GetComponentInChildren<InventoryItem>();
            if (itemInSlot == null)
            {
                AddIntoInventory(item, slot);
                return true;
            }
        }

        return false;
    }
    
    [Client]
    public void AddIntoInventory(Item item, InventorySlot slot)
    {
        // Add an object into the player's ui slot.
        GameObject itemGameObject = Instantiate(item.refOnInventory, slot.transform);
        itemGameObject.SetActive(true);

    }
    
    [Client]
    public Item GetSelectedItem(bool use)
    {
        // Return the item in hands
        InventorySlot slot = inventorySlots[selectedSlot];
        InventoryItem itemInSlot = slot.GetComponentInChildren<InventoryItem>();
        if (itemInSlot != null)
        {
            if (use) ///////// ITEM.USE
            {
                itemInSlot.item.Quantity--;
                if (itemInSlot.item.Quantity <= 0)
                {
                    Destroy(itemInSlot.gameObject);
                }
                else
                {
                    itemInSlot.RefreshCount();
                }
            }

            return itemInSlot.item;
        }

        return null;
    }

    [Client]
    public InventoryItem FindItem(Item item)
    {
        // Retrieve the InventoryItem from the player's UI
        foreach (InventorySlot slot in inventorySlots)
        {
            InventoryItem invItem = slot.GetComponentInChildren<InventoryItem>();

            if (invItem is not null)
            {
                if (invItem.item == item)
                {
                    return invItem;
                }
            }
        }

        return null;
    }

    [Client]
    public void DropItem(InventoryItem invitem)
    {
        // Remove an item from the player's inventory UI
        Destroy(invitem.gameObject);
    }

    [Server]
    public void ExportInventory(int id, string data)
    {
        SqLiteHandler.Instance.UpdateUser(id,"inventory", data);
    }
    
    [Command]
    public void ImportInventory(NetworkIdentity identity)
    {
        Player targetPlayer = identity.GetComponent<Player>();
        SQLiteDataReader userData = SqLiteHandler.Instance.GetUser(targetPlayer.id);
        userData.Read();
        ListWrapper listWrapper = JsonUtility.FromJson<ListWrapper>(userData["inventory"].ToString());
        foreach (var itemSer in listWrapper.list)
        {
            int id = IndexOfItem(itemSer.id);

            NetworkIdentity newNet;
            if (targetPlayer.inventoryManager.ServerGiveItem(identity.connectionToClient, id, itemSer.Quantity, out newNet))
            {
                ResponseInventoryAction response = new ResponseInventoryAction()
                    { success = true, action = InvAction.Give, identity = newNet };
                identity.connectionToClient.Send(response);
            }
        }
        
        
    }

    public int IndexOfItem(int UID)
    {
        for (int i = 0; i < NetManager.Instance.spawnPrefabs.Count; i++)
        {
            GameObject spawnable = NetManager.Instance.spawnPrefabs[i];
            Item item = spawnable.GetComponent<Item>();
            if (item is not null)
            {
                if (item.UID == UID)
                {
                    return i;
                }
            }
        }

        return -1;
    }
    
    public string ConvertInventory(List<Item> inv)
    {
        List<ItemSerialize> items = new List<ItemSerialize>();
        for (int i = 0; i < inv.Count; i++)
        {
            Item item = inv[i];
            
            ItemSerialize ser = new ItemSerialize();
            ser.Quantity = item.Quantity;
            ser.id = item.UID;
                
            items.Add(ser);
        }

        ListWrapper listWrapper = new ListWrapper();
        listWrapper.list = items;
        
        return JsonUtility.ToJson(listWrapper);
    }
    
    
    public Item ConvertFromNetID(NetworkIdentity identity)
    {
        return identity.GetComponent<Item>();
    }
}