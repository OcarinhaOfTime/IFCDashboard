using UnityEngine;

public class Rotator : MonoBehaviour
{
    public float rotationSpeed = 100f;

    // Update is called once per frame
    void Update()
    {
        transform.rotation *= Quaternion.Euler(new Vector3(0, 0, 45) * rotationSpeed * Time.deltaTime);
    }
}
