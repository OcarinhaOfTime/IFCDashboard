using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class LoadSceneScript : MonoBehaviour
{    
    [SerializeField]
    private string sceneName;

    private Button button;

    private void Awake()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(LoadConfiguredScene);
    }

    private void OnDestroy()
    {
        if (button != null)
        {
            button.onClick.RemoveListener(LoadConfiguredScene);
        }
    }

    public void LoadConfiguredScene()
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            Debug.LogError(
                "No destination scene was configured.",
                this
            );

            return;
        }

        SceneManager.LoadScene(
            sceneName.Trim(),
            LoadSceneMode.Single
        );
    }
}
