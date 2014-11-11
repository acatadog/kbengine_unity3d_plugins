using UnityEngine;
using System;
using System.Collections;
using KBEngine;

public class ClientAppNoThread : MonoBehaviour 
{
	public static KBEngineApp gameapp = null;
	
	void Awake() 
	 {
		DontDestroyOnLoad(transform.gameObject);
	 }
 
	// Use this for initialization
	void Start () 
	{
		MonoBehaviour.print("clientapp::start()");
		installEvents();
		initKBEngine();
	}
	
	void installEvents()
	{
	}
	
	void initKBEngine()
	{
		gameapp = new KBEngineApp(Application.persistentDataPath, "127.0.0.1", 20013, 5);
	}
	
	void OnDestroy()
	{
		MonoBehaviour.print("clientapp::OnDestroy(): begin");
		KBEngineApp.app.destroy();
		MonoBehaviour.print("clientapp::OnDestroy(): over");
	}
	
	void FixedUpdate () {
		KBEUpdate();
	}
		
	void KBEUpdate()
	{
		gameapp.process();
		KBEngine.Event.processOutEvents();
	}
}
