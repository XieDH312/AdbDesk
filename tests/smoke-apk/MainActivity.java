package com.adbdesk.smoketest;

import android.app.Activity;
import android.os.Bundle;
import android.view.Gravity;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

public class MainActivity extends Activity {
    private int clicks = 0;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER);
        root.setPadding(40, 40, 40, 40);
        root.setBackgroundColor(0xfff1f5f9);
        TextView title = new TextView(this);
        title.setText("AdbDesk 连接测试");
        title.setTextSize(26);
        title.setGravity(Gravity.CENTER);
        TextView status = new TextView(this);
        status.setText("点击次数：0");
        status.setTextSize(22);
        status.setGravity(Gravity.CENTER);
        Button button = new Button(this);
        button.setText("点击验证手机控制");
        button.setOnClickListener(v -> status.setText("点击次数：" + (++clicks)));
        root.addView(title);
        root.addView(status);
        root.addView(button);
        setContentView(root);
    }
}
