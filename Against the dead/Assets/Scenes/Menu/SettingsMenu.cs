using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

public class SettingsMenu : MonoBehaviour
{
    public AudioMixer audioMixer;

    public Player player;
    
    public void SetVolume(float volume)
    {
        audioMixer.SetFloat("Volume", volume);
    }

    public void SetSensi(float sensi)
    {
        player.playerController.SetSensitivity(sensi);
        Debug.Log(sensi);
    }
    
    
}
