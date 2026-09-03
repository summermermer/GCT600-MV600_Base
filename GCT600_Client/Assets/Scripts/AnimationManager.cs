using UnityEngine;

public class AnimationManager : MonoBehaviour
{
    public GameObject Avatar;
    public float animateThreshold = 1.0f;

    private Animator animator;
    private bool isClose;

    void Start()
    {
        animator = GetComponentInChildren<Animator>();
    }

    void Update()
    {
        if (Avatar == null || animator == null)
            return;

        float distance = Vector3.Distance(transform.position, Avatar.transform.position);
        bool wasClose = isClose;
        isClose = distance <= animateThreshold;
        Debug.Log(distance);
        if (isClose != wasClose)
        {
            Debug.Log("SetBool IsClose: " + isClose);
            animator.SetBool("IsClose", isClose);
        }
    }
}
