using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameStart : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        SceneLoader();
        Cursor.lockState = CursorLockMode.None;
    }

    void SceneLoader()
    {
        SceneManager.LoadScene("Main", LoadSceneMode.Additive);
    }
}
