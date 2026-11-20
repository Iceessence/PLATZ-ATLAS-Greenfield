#version 450

layout(location = 0) in vec3 vertexColor;
layout(location = 1) in vec2 uv;
layout(location = 2) flat in ivec3 image;
layout(binding = 0) uniform samplerBuffer imageStrip;
layout(location = 0) out vec4 fragmentColor;

void main()
{
    if (image.y == 0)
    {
        fragmentColor = vec4(vertexColor, 1.0);
        return;
    }
    ivec2 pixel = clamp(ivec2(uv * vec2(image.yz)), ivec2(0), image.yz - 1);
    fragmentColor = texelFetch(imageStrip, image.x + pixel.y * image.y + pixel.x);
    if (vertexColor.r == 1.0)
    {
        vec3 channel = fragmentColor.rgb;
        fragmentColor.rgb = mix(pow((channel + 0.055) / 1.055, vec3(2.4)), channel / 12.92, lessThanEqual(channel, vec3(0.04045)));
    }
}
